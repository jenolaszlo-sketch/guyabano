using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Session;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Manages the transactional session workspace: isolated staging mutations are
/// validated against an exact baseline revision and only promoted when the
/// baseline has not advanced (concurrent-mutation fencing). Failed or stale
/// mutations never alter the current workspace.
/// </summary>
public sealed class CodeGenerationStagingService(
    CodeGenerationWorkspaceResolver workspaceResolver,
    IGuyabanoSessionStore sessionStore,
    IArtifactRepository artifactRepository,
    ISessionEventStore sessionEvents,
    IOptions<CodeGenerationWorkerOptions> options,
    ICrossStoreOperationStore? operationStore = null)
{
    public async Task<WorkspaceStagingMutation> CreateStagingAsync(
        Guid sessionId,
        string mutationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationId);

        var session = await sessionStore.GetAsync(
                new GuyabanoSessionId(sessionId),
                cancellationToken)
            .ConfigureAwait(false) ??
            throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");

        var workspace = workspaceResolver.Resolve(new GuyabanoSessionId(sessionId));
        var staging = workspaceResolver.ResolveStaging(new GuyabanoSessionId(sessionId), mutationId);
        var stagingRoot = workspaceResolver.ResolveStagingRoot(new GuyabanoSessionId(sessionId), mutationId);
        if (Directory.Exists(staging.HostPath))
            throw new InvalidOperationException(
                $"Staging mutation '{mutationId}' already exists for session '{sessionId}'.");

        var baseline = session.CurrentWorkspaceRevision;
        if (baseline is null)
        {
            baseline = await ComputeWorkspaceRevisionAsync(
                    workspace.HostPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var initialized = await sessionStore.UpdateWorkspaceRevisionAsync(
                    session.Id,
                    expectedRevision: null,
                    baseline,
                    cancellationToken)
                .ConfigureAwait(false);
            if (initialized is null)
            {
                session = await sessionStore.GetAsync(session.Id, cancellationToken)
                    .ConfigureAwait(false) ??
                    throw new KeyNotFoundException(
                        $"Session '{sessionId}' no longer exists.");
                baseline = session.CurrentWorkspaceRevision ??
                    throw new ConcurrentWorkspaceMutationException(
                        $"Session '{sessionId}' baseline could not be initialized.");
            }
        }
        Directory.CreateDirectory(stagingRoot);
        if (Directory.Exists(workspace.HostPath))
            CopyDirectory(workspace.HostPath, staging.HostPath);

        return new WorkspaceStagingMutation(
            SessionId: sessionId,
            MutationId: mutationId,
            BaselineRevision: baseline,
            CreatedAt: DateTimeOffset.UtcNow,
            StagingHostPath: staging.HostPath);
    }

    public async Task<WorkspacePromotion> ValidateAndPromoteAsync(
        Guid sessionId,
        string mutationId,
        string expectedBaselineRevision,
        Func<string, CancellationToken, Task<StagingValidationResult>> validate,
        CancellationToken cancellationToken = default) =>
        await ValidateAndPromoteAsync(
            sessionId,
            mutationId,
            expectedBaselineRevision,
            operationId: null,
            validate,
            cancellationToken).ConfigureAwait(false);

    public async Task<WorkspacePromotion> ValidateAndPromoteAsync(
        Guid sessionId,
        string mutationId,
        string expectedBaselineRevision,
        CrossStoreOperationId? operationId,
        Func<string, CancellationToken, Task<StagingValidationResult>> validate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBaselineRevision);
        ArgumentNullException.ThrowIfNull(validate);

        var session = await sessionStore.GetAsync(
                new GuyabanoSessionId(sessionId),
                cancellationToken)
            .ConfigureAwait(false) ??
            throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
        var workspace = workspaceResolver.Resolve(new GuyabanoSessionId(sessionId));
        var staging = workspaceResolver.ResolveStaging(new GuyabanoSessionId(sessionId), mutationId);
        var latestRunId = session.WorkflowRunIds.LastOrDefault();
        if (!Directory.Exists(staging.HostPath))
        {
            var recovered = latestRunId == Guid.Empty
                ? null
                : await artifactRepository.ReadLatestAsync<WorkspacePromotion>(
                    latestRunId.ToString("D"),
                    "workspace-promotion",
                    mutationId,
                    cancellationToken).ConfigureAwait(false);
            if (recovered is null || !recovered.Payload.Validated ||
                recovered.Payload.FromRevision != expectedBaselineRevision ||
                session.CurrentWorkspaceRevision != recovered.Payload.ToRevision)
            {
                throw new InvalidOperationException(
                    $"Staging mutation '{mutationId}' does not exist for session '{sessionId}'.");
            }
            await RecordPromotionAsync(
                recovered.Payload,
                latestRunId,
                operationId,
                cancellationToken).ConfigureAwait(false);
            return recovered.Payload;
        }
        if (!string.Equals(
                session.CurrentWorkspaceRevision,
                expectedBaselineRevision,
                StringComparison.Ordinal))
        {
            throw new ConcurrentWorkspaceMutationException(
                $"Session '{sessionId}' workspace advanced from baseline '{expectedBaselineRevision}' to '{session.CurrentWorkspaceRevision}'; staging '{mutationId}' cannot be promoted over a changed baseline.");
        }

        var validation = await validate(staging.HostPath, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.Valid)
        {
            await DiscardAsync(sessionId, mutationId, cancellationToken)
                .ConfigureAwait(false);
            throw new StagingValidationException(
                validation.Reason ?? $"Staging mutation '{mutationId}' failed validation and was discarded.");
        }

        var toRevision = await ComputeWorkspaceRevisionAsync(
                staging.HostPath,
                cancellationToken)
            .ConfigureAwait(false);
        var backupPath = Path.Combine(
            options.Value.OutputRoot,
            "sessions",
            sessionId.ToString("D"),
            "backups",
            $"{mutationId}-{Guid.NewGuid():N}");
        var stagingRoot = workspaceResolver.ResolveStagingRoot(
            new GuyabanoSessionId(sessionId),
            mutationId);

        // Atomic-ish promotion: rename current workspace to backup, promote staging
        // into place, then advance the session revision with compare-and-swap. On CAS
        // conflict (concurrent promotion) we roll back to the prior workspace.
        try
        {
            if (Directory.Exists(workspace.HostPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                Directory.Move(workspace.HostPath, backupPath);
            }

            Directory.Move(staging.HostPath, workspace.HostPath);

            var updated = await sessionStore.UpdateWorkspaceRevisionAsync(
                    new GuyabanoSessionId(sessionId),
                    expectedBaselineRevision,
                    toRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (updated is null)
            {
                Directory.Move(workspace.HostPath, staging.HostPath);
                if (Directory.Exists(backupPath))
                    Directory.Move(backupPath, workspace.HostPath);
                throw new ConcurrentWorkspaceMutationException(
                    $"Session '{sessionId}' workspace was concurrently advanced while promoting '{mutationId}'; rollback completed, nothing promoted.");
            }
        }
        catch
        {
            if (Directory.Exists(workspace.HostPath) &&
                !Directory.Exists(staging.HostPath))
            {
                Directory.Move(workspace.HostPath, staging.HostPath);
            }

            if (Directory.Exists(backupPath) &&
                !Directory.Exists(workspace.HostPath))
            {
                Directory.Move(backupPath, workspace.HostPath);
            }

            throw;
        }

        if (Directory.Exists(stagingRoot))
            TryDeleteDirectory(stagingRoot);

        var promotion = new WorkspacePromotion(
            SessionId: sessionId,
            MutationId: mutationId,
            FromRevision: expectedBaselineRevision,
            ToRevision: toRevision,
            Validated: true,
            PromotedAt: DateTimeOffset.UtcNow,
            BackupPath: Directory.Exists(backupPath) ? backupPath : null);

        if (latestRunId != Guid.Empty)
        {
            await artifactRepository.WriteAsync(
                new ArtifactWriteRequest<WorkspacePromotion>(
                    WorkflowId: latestRunId.ToString("D"),
                    Kind: "workspace-promotion",
                    SchemaVersion: 1,
                    StageKey: mutationId,
                    Status: ArtifactStatus.Approved,
                    Payload: promotion)
                {
                    SessionId = sessionId.ToString("D")
                },
                cancellationToken).ConfigureAwait(false);
        }

        await sessionEvents.AppendAsync(new Guyabano.Session.SessionEventRequest(
                new GuyabanoSessionId(sessionId),
                Actor: "guyabano",
                EventType: Guyabano.Session.SessionEventTypes.WorkspacePromoted,
                OccurredAt: promotion.PromotedAt,
                CorrelationId: latestRunId,
                CrossSystemRefs: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId.ToString("D"),
                    ["mutationId"] = mutationId,
                    ["fromRevision"] = promotion.FromRevision,
                    ["toRevision"] = promotion.ToRevision
                },
                PayloadJson: System.Text.Json.JsonSerializer.Serialize(promotion, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                IdempotencyKey: $"workspace-promotion:{sessionId:D}:{mutationId}:{toRevision}"))
            .ConfigureAwait(false);

        await RecordPromotionAsync(
            promotion,
            latestRunId,
            operationId,
            cancellationToken).ConfigureAwait(false);

        return promotion;
    }

    private async Task RecordPromotionAsync(
        WorkspacePromotion promotion,
        Guid workflowRunId,
        CrossStoreOperationId? operationId,
        CancellationToken cancellationToken)
    {
        if (operationStore is null || operationId is null)
            return;
        var operation = await operationStore.GetAsync(
                operationId.Value,
                cancellationToken)
            .ConfigureAwait(false) ??
            throw new KeyNotFoundException(
                $"Operation '{operationId}' does not exist.");
        if (operation.SessionId.Value != promotion.SessionId)
            throw new InvalidOperationException(
                "Workspace promotion operation belongs to another session.");
        var participant = $"workspace-promotion:{promotion.MutationId}";
        var receipt = new CrossStoreParticipantReceipt
        {
            Participant = participant,
            IdempotencyKey = operation.ParticipantIdempotencyKey(participant),
            State = CrossStoreParticipantState.Applied,
            RecordedAt = promotion.PromotedAt,
            BeforeIdentity = promotion.FromRevision,
            AfterIdentity = promotion.ToRevision,
            ResultHash = promotion.ToRevision,
            RecoveryAction =
                "Verify the session CAS revision and workspace hash, then replay downstream publications."
        };
        operation = await operationStore.RecordParticipantAsync(
                operation.Id,
                receipt,
                cancellationToken)
            .ConfigureAwait(false);
        operation = await operationStore.TransitionAsync(
                operation.Id,
                CrossStoreOperationState.WorkspacePromoted,
                promotion.PromotedAt,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await sessionEvents.AppendAsync(
            new SessionEventRequest(
                operation.SessionId,
                "guyabano",
                SessionEventTypes.OperationTransitioned,
                promotion.PromotedAt,
                CorrelationId: workflowRunId == Guid.Empty
                    ? operation.WorkflowRunId
                    : workflowRunId,
                CrossSystemRefs: new Dictionary<string, string>
                {
                    ["operationId"] = operation.Id.ToString(),
                    ["operationState"] = operation.State.ToString(),
                    ["participant"] = participant,
                    ["fromRevision"] = promotion.FromRevision,
                    ["toRevision"] = promotion.ToRevision
                },
                IdempotencyKey:
                    $"{operation.IdempotencyKey}:event:WorkspacePromoted:{participant}"),
            cancellationToken).ConfigureAwait(false);
    }

    public Task DiscardAsync(
        Guid sessionId,
        string mutationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationId);
        var stagingRoot = workspaceResolver.ResolveStagingRoot(
            new GuyabanoSessionId(sessionId),
            mutationId);
        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);
        return Task.CompletedTask;
    }

    private static async Task<string> ComputeWorkspaceRevisionAsync(
        string workspaceHostPath,
        CancellationToken cancellationToken)
    {
        var snapshot = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(
            workspaceHostPath,
            cancellationToken).ConfigureAwait(false);
        var ordered = snapshot
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value.Hash}");
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", ordered))))
            .ToLowerInvariant();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; the backup and promotion artifact remain authoritative.
        }
    }
}
