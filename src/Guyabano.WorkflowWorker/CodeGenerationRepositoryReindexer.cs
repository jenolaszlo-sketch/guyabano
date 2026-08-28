using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Penghou.Cangjie;
using Penghou.Hetu;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationRepositoryReindexer(
    HetuHost hetu,
    IContextStore contextStore,
    IArtifactRepository artifactRepository,
    CodeGenerationWorkspaceResolver workspaceResolver,
    IOptions<CodeGenerationWorkerOptions> options)
{
    public async Task<RepositoryReindexReceipt> ReindexAsync(
        RepositoryReindexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowId);

        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            request.WorkflowId,
            cancellationToken).ConfigureAwait(false);
        var settings = options.Value;
        var repositoryId = settings.RepositoryContextEnabled &&
            !string.IsNullOrWhiteSpace(settings.RepositoryId)
            ? settings.RepositoryId
            : $"guyabano:session:{workspace.SessionId}";
        var zhinuContext = CodeGenerationZhinuStepScope.Current;
        var stepRevision = zhinuContext?.Revision ?? 1;
        // Hetu index runs are immutable; keying the run by step revision guarantees
        // a unique run per attempt so Zhinu step retries never collide.
        var runId = new CodeIndexRunId(
            $"guyabano:{request.WorkflowId}:post-generation:v{stepRevision}");
        var result = await hetu.IndexRepositoryAsync(
            new CodeRepositoryDescriptor(
                new CodeRepositoryId(repositoryId),
                workspace.HostPath),
            runId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var publication = result.Publication;
        var diagnostics = result.Diagnostics;
        var publishedState = result.PublishedState;

        var stepKey = zhinuContext?.StepKey ?? "repository/reindex-post-generation";
        var workspaceRevision = await ComputeWorkspaceRevisionAsync(
                workspace.HostPath,
                cancellationToken)
            .ConfigureAwait(false);

        var payload = new RepositoryReindexPublicationPayload(
            RepositoryId: repositoryId,
            Location: workspace.HostPath,
            IndexRunId: publication.IndexRunId.Value,
            IndexIdentity: publication.IndexIdentity.Value,
            ProviderSnapshotIdentity: publishedState.SnapshotIdentity,
            IsConsistentSnapshot: publishedState.IsConsistentSnapshot,
            FilesDiscovered: diagnostics.FilesDiscovered,
            FilesNew: diagnostics.FilesNew,
            FilesChanged: diagnostics.FilesChanged,
            FilesUnchanged: diagnostics.FilesUnchanged,
            FilesDeleted: diagnostics.FilesDeleted,
            NodesProduced: diagnostics.NodesProduced,
            SessionId: workspace.SessionId.ToString(),
            WorkflowRunId: request.WorkflowId,
            StepKey: stepKey,
            StepRevision: stepRevision,
            PublishedAt: DateTimeOffset.UtcNow,
            WorkspaceRevisionId: workspaceRevision);

        await artifactRepository.WriteAsync(
            new ArtifactWriteRequest<RepositoryReindexPublicationPayload>(
                WorkflowId: request.WorkflowId,
                Kind: "repository-publication",
                SchemaVersion: 2,
                StageKey: "post-generation",
                Status: ArtifactStatus.Validated,
                Payload: payload)
            {
                SessionId = workspace.SessionId.ToString()
            },
            cancellationToken).ConfigureAwait(false);

        var summaryContent =
            $"Post-generation Hetu publication: repository '{repositoryId}' indexed at identity {publication.IndexIdentity.Value}. " +
            $"{diagnostics.FilesDiscovered} files discovered, {diagnostics.FilesNew} new, {diagnostics.FilesChanged} changed, " +
            $"{diagnostics.FilesUnchanged} unchanged, {diagnostics.FilesDeleted} deleted, {diagnostics.NodesProduced} nodes produced.";
        var scope = $"guyabano:session:{workspace.SessionId}";
        var hash = Hash(summaryContent);
        await contextStore.StoreAsync(
            new ContextItem
            {
                Scope = scope,
                Key = $"publication:{publication.IndexRunId.Value}",
                Kind = ContextKinds.Summary,
                Content = summaryContent,
                Provenance = new ContextProvenance
                {
                    Producer = "guyabano:repository-reindexer",
                    ProducerVersion = "1",
                    Source = new ContextSource
                    {
                        Uri = $"guyabano://session/{workspace.SessionId}/publication/{publication.IndexRunId.Value}",
                        Kind = "hetu-publication",
                        ContentHash = hash
                    }
                },
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["repositoryId"] = repositoryId,
                    ["sessionId"] = workspace.SessionId.ToString(),
                    ["indexRunId"] = publication.IndexRunId.Value,
                    ["indexIdentity"] = publication.IndexIdentity.Value,
                    ["filesUnchanged"] = diagnostics.FilesUnchanged.ToString(),
                    ["nodesProduced"] = diagnostics.NodesProduced.ToString()
                },
                Tags = ["hetu-publication", $"session:{workspace.SessionId}", $"repository:{repositoryId}"]
            },
            new ContextWriteOptions
            {
                IdempotencyKey = $"publication:{publication.IndexRunId.Value}"
            },
            cancellationToken).ConfigureAwait(false);

        return new RepositoryReindexReceipt(
            RepositoryId: repositoryId,
            IndexRunId: publication.IndexRunId.Value,
            IndexIdentity: publication.IndexIdentity.Value,
            SnapshotIdentity: publishedState.SnapshotIdentity,
            IsConsistentSnapshot: publishedState.IsConsistentSnapshot,
            FilesDiscovered: diagnostics.FilesDiscovered,
            FilesNew: diagnostics.FilesNew,
            FilesChanged: diagnostics.FilesChanged,
            FilesUnchanged: diagnostics.FilesUnchanged,
            FilesDeleted: diagnostics.FilesDeleted,
            NodesProduced: diagnostics.NodesProduced,
            CompletedAt: DateTimeOffset.UtcNow);
    }

    private static string Hash(string content) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    private static async Task<string> ComputeWorkspaceRevisionAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var snapshot = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(
                workspacePath,
                cancellationToken)
            .ConfigureAwait(false);
        var canonical = string.Join(
            "|",
            snapshot.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value.Hash}"));
        return Hash(canonical);
    }
}
