using System.Security.Cryptography;
using System.Text;
using Guyabano.CodeGeneration.Workflows;
using Penghou.Cangjie;
using Penghou.Hetu;

namespace Guyabano.WorkflowWorker;

internal sealed class RepositoryContextService(
    HetuHost hetu,
    IContextStore contextStore) : IRepositoryContextService
{
    internal const string SelectionStrategy = "hetu-public-surface-and-symbol-neighborhood";
    internal const string SelectionStrategyVersion = "1";
    private const int MaximumProjects = 12;
    private const int MaximumSymbolsPerProject = 100;
    private const int MaximumSeedCandidates = 5;
    private const int MaximumMemoryItems = 12;
    private const int MaximumMemoryQueryCharacters = 2_000;

    public async Task<RepositoryRevision> IndexAsync(
        RepositoryIndexRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);

        var attempt = CodeGenerationActivityExecutionContext.Current.Info.Attempt;
        var runId = new CodeIndexRunId(
            $"guyabano:{request.WorkflowRunId}:repository-index:{attempt}");
        var result = await hetu.IndexRepositoryAsync(
            new CodeRepositoryDescriptor(
                new CodeRepositoryId(request.Repository.RepositoryId),
                request.Repository.Location),
            runId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var publication = result.Publication;
        var sources = result.PublishedState.Sources;
        return new RepositoryRevision(
            publication.RepositoryId.Value,
            request.Repository.Location,
            publication.IndexIdentity.Value,
            publication.IndexRunId.Value,
            publication.SnapshotIdentity,
            publication.IsConsistentSnapshot,
            result.Diagnostics.FilesDiscovered,
            result.Diagnostics.FilesNew +
                result.Diagnostics.FilesChanged +
                result.Diagnostics.FilesDeleted,
            sources
                .Select(item => item.SourcePath)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    public async Task<RepositoryContextSelection> SelectAsync(
        RepositoryContextSelectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var repositoryId = new CodeRepositoryId(request.Revision.RepositoryId);
        var publication = new CodeGraphPublication(
            repositoryId,
            new CodeIndexRunId(request.Revision.IndexRunId),
            request.Revision.ProviderSnapshotIdentity,
            request.Revision.IsConsistentSnapshot,
            new CodeIndexIdentity(request.Revision.WorkspaceRevision));
        var queries = hetu.Queries.Bind(publication);
        var observations = new List<RepositoryContextObservation>
        {
            CreateIndexSummary(
                request.Revision,
                request.Revision.SourcePaths.Count)
        };
        var seeds = request.SymbolSeeds
            .Where(seed => !string.IsNullOrWhiteSpace(seed))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lookup = await queries.FindSymbolAsync(
                seed,
                cancellationToken).ConfigureAwait(false);
            foreach (var candidate in lookup.Result.Candidates
                         .Take(MaximumSeedCandidates))
            {
                var neighborhood = await queries.GetNeighborhoodAsync(
                        candidate.Id,
                        options: new CodeGraphQueryOptions(
                            maxDepth: 2,
                            maxNodes: 75,
                            maxEdges: 150),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                observations.Add(CreateSymbolObservation(
                    seed,
                    candidate,
                    neighborhood.Result,
                    request.Revision));
            }
        }

        if (seeds.Length == 0)
        {
            var projectPaths = request.Revision.SourcePaths
                .Where(path => path.EndsWith(
                    ".csproj",
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(MaximumProjects)
                .ToArray();
            foreach (var projectPath in projectPaths)
            {
                var nodes = await GetProjectPublicSurfaceAsync(
                    queries,
                    projectPath,
                    cancellationToken).ConfigureAwait(false);
                observations.Add(CreateProjectObservation(
                    projectPath,
                    nodes,
                    request.Revision));
            }
        }

        return new RepositoryContextSelection(
            request.Revision,
            SelectionStrategy,
            SelectionStrategyVersion,
            observations);
    }

    private async Task<IReadOnlyList<CodeGraphNode>>
        GetProjectPublicSurfaceAsync(
            CodeGraphPublicationQuery queries,
            string projectPath,
            CancellationToken cancellationToken) =>
        (await queries.GetPublicSurfaceAsync(
            projectPath,
            new CodeGraphQueryOptions(
                maxDepth: 10,
                maxNodes: MaximumSymbolsPerProject + 2,
                maxEdges: 1000),
            cancellationToken).ConfigureAwait(false)).Result;

    public async Task<RepositoryContextReference> CaptureAsync(
        RepositoryContextCaptureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        var selection = request.Selection;
        var revision = selection.Revision;
        var scope = RepositoryScope(revision.RepositoryId);
        var writes = selection.Observations
            .Select(observation => new ContextWriteRequest
            {
                Item = new ContextItem
                {
                    Scope = scope,
                    Key = $"hetu:{observation.Key}",
                    Kind = ContextKinds.Summary,
                    Content = observation.Content,
                    Provenance = new ContextProvenance
                    {
                        Source = new ContextSource
                        {
                            Uri = observation.SourceUri,
                            Kind = "hetu-code-graph",
                            ContentHash = Hash(observation.Content)
                        },
                        Producer = "guyabano:hetu-context-selector",
                        ProducerVersion = selection.StrategyVersion,
                        Attributes = ProvenanceAttributes(
                            revision,
                            request.WorkflowRunId,
                            request.SessionId)
                    },
                    Metadata = MergeMetadata(
                        observation.Metadata,
                        revision,
                        request.SessionId),
                    Tags =
                    [
                        "repository-context",
                        $"repository:{revision.RepositoryId}",
                        $"session:{request.SessionId}",
                        $"workspace-revision:{revision.WorkspaceRevision}"
                    ]
                },
                Options = new ContextWriteOptions
                {
                    IdempotencyKey =
                        $"hetu:{revision.IndexRunId}:{observation.Key}"
                }
            })
            .ToArray();
        var selected = (await contextStore.StoreBatchAsync(
            writes,
            cancellationToken).ConfigureAwait(false)).ToList();

        if (!string.IsNullOrWhiteSpace(request.QueryText))
        {
            var queryText = request.QueryText.Length <= MaximumMemoryQueryCharacters
                ? request.QueryText
                : request.QueryText[..MaximumMemoryQueryCharacters];
            var memoryScopes = new[]
            {
                $"guyabano:session:{request.SessionId}",
                $"guyabano:session:{request.SessionId}:repository:{revision.RepositoryId}"
            };
            var memoryItems = new List<ContextItem>();
            foreach (var memoryScope in memoryScopes)
            {
                var memories = await contextStore.SearchAsync(
                    new ContextQuery
                    {
                        Text = queryText,
                        Scope = memoryScope,
                        Kinds =
                        [
                            ContextKinds.Decision,
                            ContextKinds.Evidence,
                            ContextKinds.Knowledge,
                            ContextKinds.Summary
                        ],
                        Limit = MaximumMemoryItems,
                        SearchMode = ContextSearchMode.AnyTerm
                    },
                    cancellationToken).ConfigureAwait(false);
                memoryItems.AddRange(memories.Select(hit => hit.Item));
            }
            selected.AddRange(memoryItems
                .DistinctBy(item => item.Id)
                .Take(MaximumMemoryItems));
        }

        selected = selected
            .DistinctBy(item => item.Id)
            .ToList();
        var snapshotId = DeterministicGuid(
            $"{request.SessionId}\n{request.WorkflowRunId}\n{revision.IndexRunId}\n{selection.Strategy}\n{selection.StrategyVersion}");
        var snapshot = await contextStore.StoreSnapshotAsync(
            new ContextSnapshot
            {
                Id = snapshotId,
                ItemIds = selected.Select(item => item.Id).ToArray(),
                QueryIdentity =
                    $"guyabano:{request.WorkflowRunId}:repository-context",
                Strategy = selection.Strategy,
                StrategyVersion = selection.StrategyVersion,
                Purpose = "code-generation-planning",
                Metadata = new Dictionary<string, string>
                {
                    ["repositoryId"] = revision.RepositoryId,
                    ["sessionId"] = request.SessionId,
                    ["workspaceRevision"] = revision.WorkspaceRevision,
                    ["hetuIndexRunId"] = revision.IndexRunId
                }
            },
            cancellationToken).ConfigureAwait(false);
        var resolved = await contextStore.ResolveSnapshotAsync(
            snapshot.Id,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException(
                "Cangjie stored a context snapshot that could not be resolved.");

        return new RepositoryContextReference(
            resolved.Snapshot.Id,
            revision,
            selection.Strategy,
            selection.StrategyVersion,
            RenderSnapshot(resolved.Items),
            resolved.Items.Count);
    }

    private static RepositoryContextObservation CreateIndexSummary(
        RepositoryRevision revision,
        int sourceCount) =>
        new(
            "index-summary",
            $"Repository '{revision.RepositoryId}' was indexed at workspace revision {revision.WorkspaceRevision}. " +
            $"Hetu observed {sourceCount} supported source contribution(s) and {revision.FilesDiscovered} repository file(s).",
            PublicationUri(revision, "index-summary"),
            new Dictionary<string, string>
            {
                ["observationKind"] = "index-summary",
                ["sourceCount"] = sourceCount.ToString()
            });

    private static RepositoryContextObservation CreateProjectObservation(
        string projectPath,
        IReadOnlyList<CodeGraphNode> nodes,
        RepositoryRevision revision)
    {
        var lines = nodes
            .OrderBy(node => node.QualifiedName ?? node.Name, StringComparer.Ordinal)
            .Select(node =>
                $"- {node.Kind.Value}: {node.QualifiedName ?? node.Name}")
            .ToArray();
        var content = lines.Length == 0
            ? $"Project {projectPath} has no indexed public symbols."
            : $"Public surface of project {projectPath}:\n{string.Join("\n", lines)}";
        return new(
            $"project:{projectPath}",
            content,
            PublicationUri(revision, $"project/{Uri.EscapeDataString(projectPath)}"),
            new Dictionary<string, string>
            {
                ["observationKind"] = "project-public-surface",
                ["projectPath"] = projectPath,
                ["symbolCount"] = nodes.Count.ToString()
            });
    }

    private static RepositoryContextObservation CreateSymbolObservation(
        string seed,
        CodeGraphNode candidate,
        CodeGraphTraversalResult neighborhood,
        RepositoryRevision revision)
    {
        var nodes = neighborhood.Nodes
            .OrderBy(node => node.QualifiedName ?? node.Name, StringComparer.Ordinal)
            .Select(node =>
                $"- {node.Kind.Value}: {node.QualifiedName ?? node.Name}");
        var edges = neighborhood.Edges
            .OrderBy(edge => edge.Id.Value, StringComparer.Ordinal)
            .Select(edge =>
                $"- {edge.Kind.Value}: {edge.SourceId.Value} -> {edge.TargetId.Value}");
        var content =
            $"Bounded code neighborhood for '{candidate.QualifiedName ?? candidate.Name}' " +
            $"(requested as '{seed}'):\nNodes:\n{string.Join("\n", nodes)}\n" +
            $"Relationships:\n{string.Join("\n", edges)}";
        return new(
            $"symbol:{candidate.Id.Value}",
            content,
            PublicationUri(revision, $"symbol/{Uri.EscapeDataString(candidate.Id.Value)}"),
            new Dictionary<string, string>
            {
                ["observationKind"] = "symbol-neighborhood",
                ["symbolSeed"] = seed,
                ["nodeId"] = candidate.Id.Value,
                ["truncated"] = neighborhood.Truncated.ToString()
            });
    }

    private static string RenderSnapshot(IReadOnlyList<ContextItem> items) =>
        string.Join(
            "\n\n",
            items.Select((item, index) =>
                $"[Repository context {index + 1}; source={item.Provenance.Source?.Uri ?? "unknown"}]\n{item.Content}"));

    private static IReadOnlyDictionary<string, string> ProvenanceAttributes(
        RepositoryRevision revision,
        string workflowRunId,
        string sessionId) =>
        new Dictionary<string, string>
        {
            ["repositoryId"] = revision.RepositoryId,
            ["workspaceRevision"] = revision.WorkspaceRevision,
            ["hetuIndexRunId"] = revision.IndexRunId,
            ["workflowRunId"] = workflowRunId,
            ["sessionId"] = sessionId,
            ["providerSnapshotIdentity"] =
                revision.ProviderSnapshotIdentity ?? string.Empty,
            ["isConsistentSnapshot"] =
                revision.IsConsistentSnapshot.ToString()
        };

    private static IReadOnlyDictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        RepositoryRevision revision,
        string sessionId)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["repositoryId"] = revision.RepositoryId,
            ["sessionId"] = sessionId,
            ["workspaceRevision"] = revision.WorkspaceRevision,
            ["hetuIndexRunId"] = revision.IndexRunId
        };
        if (metadata is not null)
        {
            foreach (var pair in metadata)
                result[pair.Key] = pair.Value;
        }
        return result;
    }

    private static string RepositoryScope(string repositoryId) =>
        $"guyabano:repository:{repositoryId}";

    private static string PublicationUri(
        RepositoryRevision revision,
        string path) =>
        $"hetu://{Uri.EscapeDataString(revision.RepositoryId)}/publication/" +
        $"{Uri.EscapeDataString(revision.IndexRunId)}/{path}";

    private static string Hash(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void Validate(RepositoryReference repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.RepositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.Location);
    }
}
