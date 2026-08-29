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
    ISessionDecisionLeaseProvider decisionLeases,
    IOptions<CodeGenerationWorkerOptions> options,
    ICrossStoreOperationStore? operationStore = null,
    SessionRecoveryCoordinator? recoveryCoordinator = null)
{
    public async Task<WorkspaceStagingMutation> CreateStagingAsync(
        Guid sessionId,
        string mutationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationId);

        var typedSessionId = new GuyabanoSessionId(sessionId);
        await using var decisionLease = await decisionLeases.AcquireAsync(
            typedSessionId,
            Guid.CreateVersion7(),
            cancellationToken).ConfigureAwait(false);

        var session = await sessionStore.GetAsync(
                typedSessionId,
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

        var typedSessionId = new GuyabanoSessionId(sessionId);
        await using var decisionLease = await decisionLeases.AcquireAsync(
            typedSessionId,
            operationId?.Value ?? Guid.CreateVersion7(),
            cancellationToken).ConfigureAwait(false);

        var session = await sessionStore.GetAsync(
                typedSessionId,
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
                await RecordDeferredRecoveryAsync(
                    session,
                    mutationId,
                    "StagingCandidateMissing",
                    SessionRecoveryAction.ReconcileForward,
                    $"Staging mutation '{mutationId}' is missing and no verified promotion receipt matches the accepted revision.",
                    cancellationToken).ConfigureAwait(false);
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
            await AbandonCandidateAsync(
                session,
                mutationId,
                "StaleStagingBaseline",
                $"Staging '{mutationId}' targets baseline '{expectedBaselineRevision}', but the accepted revision is '{session.CurrentWorkspaceRevision}'.",
                cancellationToken).ConfigureAwait(false);
            throw new ConcurrentWorkspaceMutationException(
                $"Session '{sessionId}' workspace advanced from baseline '{expectedBaselineRevision}' to '{session.CurrentWorkspaceRevision}'; staging '{mutationId}' cannot be promoted over a changed baseline.");
        }

        StagingValidationResult validation;
        try
        {
            validation = await validate(staging.HostPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryRecordDeferredRecoveryAsync(
                session,
                mutationId,
                "StagingValidationCancelled",
                SessionRecoveryAction.HaltMutation,
                $"Validation of staging mutation '{mutationId}' was cancelled; the accepted workspace remains unchanged.")
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await RecordDeferredRecoveryAsync(
                session,
                mutationId,
                exception is TimeoutException
                    ? "StagingValidationTimedOut"
                    : "StagingValidationProviderFailed",
                SessionRecoveryAction.RetryIdempotently,
                $"Validation of staging mutation '{mutationId}' failed with {exception.GetType().Name}; the accepted workspace remains unchanged.",
                cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (!validation.Valid)
        {
            await AbandonCandidateAsync(
                session,
                mutationId,
                "StagingValidationRejected",
                validation.Reason ?? $"Staging mutation '{mutationId}' failed validation.",
                cancellationToken)
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
        var promotedAt = DateTimeOffset.UtcNow;
        var transactionalAudit = sessionStore as ISessionWorkspacePromotionCommitStore;

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

            var updated = transactionalAudit is null
                ? await sessionStore.UpdateWorkspaceRevisionAsync(
                    new GuyabanoSessionId(sessionId),
                    expectedBaselineRevision,
                    toRevision,
                    cancellationToken).ConfigureAwait(false)
                : await transactionalAudit.CommitWorkspacePromotionAsync(
                    new GuyabanoSessionId(sessionId),
                    expectedBaselineRevision,
                    toRevision,
                    mutationId,
                    latestRunId == Guid.Empty ? null : latestRunId,
                    promotedAt,
                    cancellationToken).ConfigureAwait(false);
            if (updated is null)
            {
                Directory.Move(workspace.HostPath, staging.HostPath);
                if (Directory.Exists(backupPath))
                    Directory.Move(backupPath, workspace.HostPath);
                await AbandonCandidateAsync(
                    session,
                    mutationId,
                    "WorkspacePromotionCasRejected",
                    $"Promotion CAS rejected staging '{mutationId}'; the prior workspace was restored.",
                    cancellationToken).ConfigureAwait(false);
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
            PromotedAt: promotedAt,
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

        if (transactionalAudit is null)
        {
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
                    IdempotencyKey: $"workspace-promotion:{sessionId:D}:{mutationId}:{toRevision}"),
                cancellationToken).ConfigureAwait(false);
        }

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

    private async Task AbandonCandidateAsync(
        GuyabanoSession session,
        string mutationId,
        string reasonCode,
        string explanation,
        CancellationToken cancellationToken)
    {
        var recovery = recoveryCoordinator ?? new SessionRecoveryCoordinator(sessionEvents);
        var (incident, plan, planned) = await PrepareRecoveryAsync(
            recovery,
            session,
            mutationId,
            reasonCode,
            SessionRecoveryAction.AbandonCandidate,
            explanation,
            automatic: true,
            cancellationToken).ConfigureAwait(false);
        var stagingRoot = workspaceResolver.ResolveStagingRoot(session.Id, mutationId);
        await recovery.ExecuteAsync(
            plan,
            planned.EventId,
            attempt: 1,
            async (_, ct) =>
            {
                await DiscardAsync(session.Id.Value, mutationId, ct).ConfigureAwait(false);
                var verified = !Directory.Exists(stagingRoot);
                return new SessionRecoveryActionReceipt(
                    DeterministicId($"receipt\n{incident.IncidentId:D}\nabandoned"),
                    SessionRecoveryAction.AbandonCandidate,
                    "workspace-staging-candidate",
                    mutationId,
                    verified
                        ? "The staging candidate is absent and the accepted workspace revision was not changed by recovery."
                        : "The staging candidate still exists.",
                    DateTimeOffset.UtcNow,
                    verified,
                    plan.CrossSystemRefs);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordDeferredRecoveryAsync(
        GuyabanoSession session,
        string mutationId,
        string reasonCode,
        SessionRecoveryAction action,
        string explanation,
        CancellationToken cancellationToken)
    {
        var recovery = recoveryCoordinator ?? new SessionRecoveryCoordinator(sessionEvents);
        var (incident, plan, planned) = await PrepareRecoveryAsync(
            recovery,
            session,
            mutationId,
            reasonCode,
            action,
            explanation,
            automatic: false,
            cancellationToken).ConfigureAwait(false);
        await recovery.CompleteAsync(
            new SessionRecoveryResolution(
                plan.RecoveryPlanId,
                incident.IncidentId,
                session.Id,
                SessionRecoveryOutcome.UserActionRequired,
                Attempt: 0,
                $"{explanation} Review the incident and explicitly retry or abandon the candidate.",
                DateTimeOffset.UtcNow,
                session.WorkflowRunIds.LastOrDefault() is var runId && runId != Guid.Empty
                    ? runId
                    : null,
                plan.CrossSystemRefs),
            planned.EventId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TryRecordDeferredRecoveryAsync(
        GuyabanoSession session,
        string mutationId,
        string reasonCode,
        SessionRecoveryAction action,
        string explanation)
    {
        try
        {
            await RecordDeferredRecoveryAsync(
                session,
                mutationId,
                reasonCode,
                action,
                explanation,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve cancellation. The unchanged staging candidate and the
            // durable Zhinu cancellation event remain discoverable for repair.
        }
    }

    private async Task<(SessionIncident Incident, SessionRecoveryPlan Plan, SessionEvent Planned)>
        PrepareRecoveryAsync(
            SessionRecoveryCoordinator recovery,
            GuyabanoSession session,
            string mutationId,
            string reasonCode,
            SessionRecoveryAction action,
            string explanation,
            bool automatic,
            CancellationToken cancellationToken)
    {
        var correlationId = session.WorkflowRunIds.LastOrDefault() is var runId &&
            runId != Guid.Empty
            ? runId
            : (Guid?)null;
        var incidentId = DeterministicId(
            $"staging-incident\n{session.Id}\n{mutationId}\n{reasonCode}");
        var references = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionId"] = session.Id.ToString(),
            ["mutationId"] = mutationId,
            ["reasonCode"] = reasonCode,
            ["safeWorkspaceRevision"] = session.CurrentWorkspaceRevision ?? "uninitialized"
        };
        var incident = new SessionIncident(
            incidentId,
            session.Id,
            reasonCode,
            SessionIncidentSeverity.Warning,
            explanation,
            DateTimeOffset.UtcNow,
            correlationId,
            references);
        var detected = await recovery.DetectAsync(incident, cancellationToken)
            .ConfigureAwait(false);
        var plan = new SessionRecoveryPlan(
            DeterministicId($"staging-plan\n{incidentId:D}\n{action}"),
            incidentId,
            session.Id,
            action,
            explanation,
            session.CurrentWorkspaceRevision,
            automatic,
            DateTimeOffset.UtcNow,
            correlationId,
            references);
        var planned = await recovery.PlanAsync(plan, detected.EventId, cancellationToken)
            .ConfigureAwait(false);
        return (incident, plan, planned);
    }

    private static Guid DeterministicId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
