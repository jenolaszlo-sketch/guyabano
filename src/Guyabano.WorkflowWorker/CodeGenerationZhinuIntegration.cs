using System.Threading;
using Penghou.Zhinu;
using Guyabano.Artifacts;
using Guyabano.Messaging;

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
    CodeGenerationWorkspaceResolver workspaceResolver) : IArtifactRepository
{
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
                    Metadata = new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["artifactId"] = envelope.Reference.ArtifactId,
                        ["status"] = envelope.Status.ToString(),
                        ["workflowId"] = envelope.WorkflowId,
                        ["sessionId"] = envelope.SessionId!,
                        ["stageKey"] = envelope.StageKey
                    }
                },
                cancellationToken);
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
