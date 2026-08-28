using Microsoft.Extensions.Options;
using Penghou.Cangjie;
using Penghou.Hetu;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationImpactAnalysisService(
    HetuHost hetu,
    IContextStore contextStore,
    IArtifactRepository artifactRepository,
    CodeGenerationWorkflowRestartService restartService,
    CodeGenerationWorkspaceResolver workspaceResolver,
    IOptions<CodeGenerationWorkerOptions> options)
{
    private static readonly Penghou.Hetu.CodeGraphQueryOptions ImpactQueryOptions = new(
        maxDepth: 6,
        maxNodes: 500,
        maxEdges: 1000);

    /// <summary>
    /// Explains why each workflow node would be invalidated and distinguishes
    /// workflow, artifact, and code-graph causes. The proposed impact is also
    /// persisted as an authoritative artifact for audit.
    /// </summary>
    public async Task<CodeGenerationImpactReport> AnalyzeAsync(
        Guid workflowRunId,
        string? targetStepKey,
        CancellationToken cancellationToken = default)
    {
        var workflowId = workflowRunId.ToString("D");
        var preview = targetStepKey is null
            ? null
            : await restartService.PreviewAsync(workflowRunId, targetStepKey, cancellationToken)
                .ConfigureAwait(false);
        var workflowInvalidated = (preview?.InvalidatedStepKeys ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var workflowReusable = preview?.ReusableStepKeys ?? [];

        var manifests = await LoadManifestsAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
        var pathToOwner = new Dictionary<string, (string TaskId, string StepKey)>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            foreach (var file in manifest.Files)
            {
                pathToOwner.TryAdd(file.RelativePath, (manifest.TaskId, manifest.StepKey));
            }
        }

        var changedFiles = manifests
            .SelectMany(manifest => manifest.Files.Select(file => (File: file, Owner: (manifest.TaskId, manifest.StepKey))))
            .Where(item => item.File.Operation is not "Created" and not "Renamed")
            .Select(item => (item.File.RelativePath, item.Owner))
            .ToList();

        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            workflowId,
            cancellationToken).ConfigureAwait(false);
        var settings = options.Value;
        var repositoryId = settings.RepositoryContextEnabled &&
            !string.IsNullOrWhiteSpace(settings.RepositoryId)
            ? settings.RepositoryId
            : $"guyabano:session:{workspace.SessionId}";
        var publication = await artifactRepository.ReadLatestAsync<RepositoryReindexPublicationPayload>(
            workflowId,
            "repository-publication",
            "post-generation",
            cancellationToken).ConfigureAwait(false);
        var indexIdentity = publication?.Payload.IndexIdentity;

        var nodes = new List<CodeGenerationImpactNode>();

        // Workflow cause
        foreach (var stepKey in workflowInvalidated)
        {
            nodes.Add(new CodeGenerationImpactNode(
                StepKey: stepKey,
                TaskId: null,
                Cause: CodeGenerationImpactCause.Workflow,
                Reason: $"Workflow dependency invalidated by restart of '{targetStepKey}'."));
        }

        // Artifact cause: a task whose own output changed
        var artifactAffected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relativePath, owner) in changedFiles)
        {
            if (!artifactAffected.Add(owner.StepKey))
                continue;
            nodes.Add(new CodeGenerationImpactNode(
                StepKey: owner.StepKey,
                TaskId: owner.TaskId,
                Cause: CodeGenerationImpactCause.Artifact,
                Reason: $"Task '{owner.TaskId}' owns file '{relativePath}' which changed since its last accepted revision.",
                FilePath: relativePath));
        }

        // Code-graph cause: dependents of changed symbols in the Hetu graph
        var codeGraphAffected = new HashSet<string>(StringComparer.Ordinal);
        var query = await hetu.Queries.OpenLatestPublicationAsync(
            new CodeRepositoryId(repositoryId),
            cancellationToken).ConfigureAwait(false);
        if (query is not null && changedFiles.Count > 0)
        {
            var nodeToFile = await BuildNodeFileMapAsync(
                query,
                pathToOwner.Keys,
                cancellationToken).ConfigureAwait(false);

            var seeds = new List<CodeNodeId>();
            foreach (var (relativePath, _) in changedFiles)
            {
                // Hetu resolves source paths relative to the indexed repository root.
                var declarations = await query.GetDeclarationsInFileAsync(
                    relativePath,
                    cancellationToken).ConfigureAwait(false);
                seeds.AddRange(declarations.Result.Select(declaration => declaration.SymbolNodeId));
            }

            if (seeds.Count > 0)
            {
                var impact = await query.GetImpactSetsAsync(
                    seeds.Distinct().ToArray(),
                    ImpactQueryOptions,
                    cancellationToken).ConfigureAwait(false);
                var impactedNodeIds = impact.Result.Results.Values
                    .SelectMany(result => result.Nodes)
                    .Select(node => node.Id.Value)
                    .Distinct(StringComparer.Ordinal);
                foreach (var nodeId in impactedNodeIds)
                {
                    if (!nodeToFile.TryGetValue(nodeId, out var relativePath))
                        continue;
                    if (!pathToOwner.TryGetValue(relativePath, out var owner))
                        continue;
                    if (!codeGraphAffected.Add(owner.StepKey))
                        continue;
                    nodes.Add(new CodeGenerationImpactNode(
                        StepKey: owner.StepKey,
                        TaskId: owner.TaskId,
                        Cause: CodeGenerationImpactCause.CodeGraph,
                        Reason: $"Code graph shows '{relativePath}' depends on a changed symbol (node {nodeId}).",
                        FilePath: relativePath,
                        HetuNodeId: nodeId));
                }
            }
        }

        var explainedNodes = nodes
            .Distinct()
            .OrderBy(node => node.StepKey, StringComparer.Ordinal)
            .ThenBy(node => Strength(node))
            .ToArray();

        var impacted = explainedNodes.Select(node => node.StepKey)
            .ToHashSet(StringComparer.Ordinal);
        var reusable = workflowReusable
            .Where(stepKey => !impacted.Contains(stepKey) &&
                !artifactAffected.Contains(stepKey) &&
                !codeGraphAffected.Contains(stepKey))
            .ToArray();

        var report = new CodeGenerationImpactReport(
            WorkflowRunId: workflowRunId,
            TargetStepKey: targetStepKey,
            IndexIdentity: indexIdentity,
            IndexRunId: publication?.Payload.IndexRunId,
            GeneratedAt: DateTimeOffset.UtcNow,
            ImpactedNodes: explainedNodes,
            ReusableStepKeys: reusable);

        await artifactRepository.WriteAsync(
            new ArtifactWriteRequest<CodeGenerationImpactReport>(
                WorkflowId: workflowId,
                Kind: "impact-analysis",
                SchemaVersion: 1,
                StageKey: targetStepKey ?? "all",
                Status: ArtifactStatus.Validated,
                Payload: report)
            {
                SessionId = workspace.SessionId.ToString()
            },
            cancellationToken).ConfigureAwait(false);

        return report;
    }

    /// <summary>
    /// Persists the proposed impact, executes the approved restart, and records
    /// an applied plan only after Zhinu accepts the mutation.
    /// </summary>
    public async Task ApplyAsync(
        Guid workflowRunId,
        string targetStepKey,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStepKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);

        var workflowId = workflowRunId.ToString("D");
        var report = await AnalyzeAsync(workflowRunId, targetStepKey, cancellationToken)
            .ConfigureAwait(false);
        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            workflowId,
            cancellationToken).ConfigureAwait(false);

        await restartService.RestartAsync(
            new RestartApproval(
                workflowRunId,
                targetStepKey,
                approvedBy,
                Approved: true,
                ApprovedAt: DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        var plan = new CodeGenerationAppliedRestartPlan(
            WorkflowRunId: workflowRunId,
            TargetStepKey: targetStepKey,
            ApprovedBy: approvedBy,
            AppliedAt: DateTimeOffset.UtcNow,
            InvalidatedStepKeys: report.InvalidatedStepKeys,
            RerunStepKeys: report.InvalidatedStepKeys,
            ReusableStepKeys: report.ReusableStepKeys,
            ImpactedNodes: report.ImpactedNodes);

        await artifactRepository.WriteAsync(
            new ArtifactWriteRequest<CodeGenerationAppliedRestartPlan>(
                WorkflowId: workflowId,
                Kind: "applied-restart-plan",
                SchemaVersion: 1,
                StageKey: targetStepKey,
                Status: ArtifactStatus.Validated,
                Payload: plan)
            {
                SessionId = workspace.SessionId.ToString()
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GeneratedFileManifest>> LoadManifestsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        var workflowId = workflowRunId.ToString("D");
        var results = await contextStore.SearchAsync(
            new ContextQuery
            {
                Text = workflowId,
                Scope = ContextIndexingArtifactRepository.DefaultScope,
                Kinds = [ContextKinds.Artifact],
                Limit = 100,
                SearchMode = ContextSearchMode.AnyTerm
            },
            cancellationToken).ConfigureAwait(false);
        var manifests = new List<GeneratedFileManifest>();
        foreach (var hit in results)
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<GeneratedFileManifest>(
                hit.Item.Content,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (manifest is not null &&
                manifest.WorkflowRunId.Equals(workflowId, StringComparison.Ordinal) &&
                manifest.Files is not null &&
                !string.IsNullOrWhiteSpace(manifest.TaskId))
            {
                manifests.Add(manifest);
            }
        }

        return manifests
            .GroupBy(manifest => manifest.TaskId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(manifest => manifest.StepRevision)
                .ThenByDescending(manifest => manifest.CreatedAt)
                .First())
            .ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, string>> BuildNodeFileMapAsync(
        CodeGraphPublicationQuery query,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relativePath in relativePaths)
        {
            // Hetu resolves source paths relative to the indexed repository root.
            var declarations = await query.GetDeclarationsInFileAsync(
                relativePath,
                cancellationToken).ConfigureAwait(false);
            foreach (var declaration in declarations.Result)
            {
                map.TryAdd(declaration.SymbolNodeId.Value, relativePath);
            }
        }

        return map;
    }

    private static int Strength(CodeGenerationImpactNode node) => node.Cause switch
    {
        CodeGenerationImpactCause.Workflow => 0,
        CodeGenerationImpactCause.Artifact => 1,
        CodeGenerationImpactCause.CodeGraph => 2,
        _ => 3
    };
}
