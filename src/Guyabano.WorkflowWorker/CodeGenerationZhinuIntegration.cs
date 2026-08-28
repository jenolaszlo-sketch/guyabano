using System.Threading;
using Penghou.Zhinu;
using Guyabano.Artifacts;
using Guyabano.Llm.Prompting;
using Guyabano.Messaging;
using Guyabano.Session;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guyabano.WorkflowWorker;

internal static class CodeGenerationZhinuStepScope
{
    private static readonly AsyncLocal<WorkflowStepContext?> CurrentContext =
        new();

    public static WorkflowStepContext? Current => CurrentContext.Value;

    public static IDisposable Push(WorkflowStepContext context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(WorkflowStepContext? previous) :
        IDisposable
    {
        public void Dispose() => CurrentContext.Value = previous;
    }
}

internal sealed class ZhinuPublishingArtifactRepository(
    IArtifactRepository inner,
    CodeGenerationWorkspaceResolver workspaceResolver,
    ICrossStoreOperationStore? operationStore = null,
    ISessionEventStore? sessionEvents = null) : IArtifactRepository
{
    /// <summary>
    /// Filesystem/Cangjie success + Zhinu publish failure leaves an authoritative
    /// file on disk. The next Zhinu step retry reuses the same content-hash file
    /// (FileSystem idempotent) and re-indexes Cangjie idempotently, then republishes.
    /// Unrecoverable filesystem errors (permission) fail fast; transient IO is retried
    /// via Zhinu step retry.
    /// </summary>
    public async Task<ArtifactEnvelope<TPayload>> WriteAsync<TPayload>(
        ArtifactWriteRequest<TPayload> request,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            request.WorkflowId,
            cancellationToken);
        request = request with
        {
            SessionId = workspace.SessionId.ToString()
        };
        var envelope = await inner.WriteAsync(request, cancellationToken);
        var context = CodeGenerationZhinuStepScope.Current;
        if (context is not null)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["artifactId"] = envelope.Reference.ArtifactId,
                ["status"] = envelope.Status.ToString(),
                ["workflowId"] = envelope.WorkflowId,
                ["sessionId"] = envelope.SessionId!,
                ["stageKey"] = envelope.StageKey,
                ["hashVersion"] = envelope.Reference.HashVersion
            };
            var envelopeOptions = new JsonSerializerOptions(
                JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            };
            envelopeOptions.Converters.Add(new JsonStringEnumConverter());
            metadata["envelopeHash"] = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        JsonSerializer.SerializeToUtf8Bytes(
                            envelope,
                            envelopeOptions)))
                .ToLowerInvariant();
            var correlation = LlmRequestCorrelationScope.Current;
            if (correlation?.CangjieSnapshotId is not null)
            {
                metadata["cangjieSnapshotId"] = correlation.CangjieSnapshotId.Value.ToString("D");
                if (correlation.CangjieStrategy is not null) metadata["cangjieStrategy"] = correlation.CangjieStrategy;
                if (correlation.CangjieStrategyVersion is not null) metadata["cangjieStrategyVersion"] = correlation.CangjieStrategyVersion;
                if (correlation.CangjieQueryIdentity is not null) metadata["cangjieQueryIdentity"] = correlation.CangjieQueryIdentity;
                if (correlation.CangjiePurpose is not null) metadata["cangjiePurpose"] = correlation.CangjiePurpose;
                if (correlation.HetuIndexRunId is not null) metadata["hetuIndexRunId"] = correlation.HetuIndexRunId;
                if (correlation.HetuIndexIdentity is not null) metadata["hetuIndexIdentity"] = correlation.HetuIndexIdentity;
                if (correlation.WorkspaceRevision is not null) metadata["workspaceRevision"] = correlation.WorkspaceRevision;
                if (correlation.WorkflowStepRevision is not null) metadata["workflowStepRevision"] = correlation.WorkflowStepRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await context.PublishArtifactAsync(
                new WorkflowArtifactDescriptor
                {
                    Name = $"{envelope.Reference.Kind}/{envelope.StageKey}",
                    ArtifactType = envelope.Reference.Kind,
                    ArtifactVersion = envelope.Reference.SchemaVersion
                        .ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    Location = envelope.Reference.RelativePath,
                    ContentHash = envelope.Reference.ContentHash,
                    Metadata = metadata
                },
                cancellationToken);

            var operation = operationStore is null
                ? null
                : await operationStore.FindByWorkflowRunAsync(
                    context.WorkflowRunId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (operation is not null && sessionEvents is not null)
            {
                var participant =
                    $"artifact-publication:{envelope.Reference.ArtifactId}";
                var receipt = new CrossStoreParticipantReceipt
                {
                    Participant = participant,
                    IdempotencyKey = operation.ParticipantIdempotencyKey(
                        participant),
                    State = CrossStoreParticipantState.Applied,
                    RecordedAt = DateTimeOffset.UtcNow,
                    AfterIdentity = envelope.Reference.RelativePath,
                    ResultHash = envelope.Reference.ContentHash,
                    RecoveryAction =
                        "Republish the immutable content-addressed artifact through the producing Zhinu step."
                };
                operation = await operationStore!.RecordParticipantAsync(
                        operation.Id,
                        receipt,
                        cancellationToken)
                    .ConfigureAwait(false);
                await sessionEvents.AppendAsync(
                    new SessionEventRequest(
                        operation.SessionId,
                        "guyabano",
                        SessionEventTypes.OperationParticipantRecorded,
                        receipt.RecordedAt,
                        CorrelationId: operation.WorkflowRunId,
                        CrossSystemRefs: new Dictionary<string, string>
                        {
                            ["operationId"] = operation.Id.ToString(),
                            ["participant"] = participant,
                            ["artifactId"] = envelope.Reference.ArtifactId,
                            ["contentHash"] = envelope.Reference.ContentHash,
                            ["workflowStepKey"] = context.StepKey
                        },
                        IdempotencyKey:
                            $"{operation.IdempotencyKey}:event:participant:{participant}"),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return envelope;
    }

    public Task<ArtifactEnvelope<TPayload>?> ReadAsync<TPayload>(
        ArtifactReference reference,
        CancellationToken cancellationToken = default) =>
        inner.ReadAsync<TPayload>(reference, cancellationToken);

    public Task<ArtifactEnvelope<TPayload>?> ReadLatestAsync<TPayload>(
        string workflowId,
        string kind,
        string stageKey,
        CancellationToken cancellationToken = default) =>
        inner.ReadLatestAsync<TPayload>(
            workflowId,
            kind,
            stageKey,
            cancellationToken);
}

internal sealed class ZhinuWorkflowProgressPublisher(
    InMemoryWorkflowProgressHub liveProgress) : IWorkflowProgressPublisher
{
    private const string DurableEventType = "guyabano-progress";

    public async Task<WorkflowProgressEntry> PublishAsync(
        string workflowId,
        WorkflowProgress progress,
        CancellationToken cancellationToken = default)
    {
        var entry = await liveProgress.PublishAsync(
            workflowId,
            progress,
            cancellationToken);
        var context = CodeGenerationZhinuStepScope.Current;
        if (context is not null)
        {
            await context.EmitAsync(
                DurableEventType,
                entry,
                cancellationToken);
        }

        return entry;
    }
}
