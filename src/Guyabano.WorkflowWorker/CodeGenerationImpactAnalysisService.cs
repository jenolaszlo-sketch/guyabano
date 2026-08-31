using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Penghou.Cangjie;
using Penghou.Hetu;
using Penghou.Zhinu;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Session;

namespace Guyabano.WorkflowWorker;

public sealed record CodeGenerationRestartApplicationResult(
    CodeGenerationImpactReport Impact,
    RestartOutcome Outcome,
    CodeGenerationAppliedRestartPlan? AppliedPlan);

public sealed class CodeGenerationImpactAnalysisService(
    HetuHost hetu,
    IContextStore contextStore,
    IArtifactRepository artifactRepository,
    CodeGenerationWorkflowRestartService restartService,
    CodeGenerationWorkspaceResolver workspaceResolver,
    IGuyabanoSessionStore sessionStore,
    ISessionDecisionLeaseProvider decisionLeases,
    SessionRecoveryCoordinator recovery,
    IOptions<CodeGenerationWorkerOptions> options,
    IAuthenticatedActorProvider? approvalActors = null)
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
        CancellationToken cancellationToken = default) =>
        (await ProposeAsync(workflowRunId, targetStepKey, cancellationToken)
            .ConfigureAwait(false)).Impact;

    public async Task<CodeGenerationImpactProposal> ProposeAsync(
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
            PreviewId: preview?.PreviewId ?? Guid.CreateVersion7(),
            WorkflowRunId: workflowRunId,
            TargetStepKey: targetStepKey,
            RestartMode: StepRestartMode.Dependents,
            WorkspaceRevision: preview?.WorkspaceRevision,
            ChangeSetHash: ComputeChangeSetHash(
                targetStepKey,
                preview?.WorkspaceRevision,
                indexIdentity,
                explainedNodes,
                reusable),
            IndexIdentity: indexIdentity,
            IndexRunId: publication?.Payload.IndexRunId,
            GeneratedAt: DateTimeOffset.UtcNow,
            ImpactedNodes: explainedNodes,
            ReusableStepKeys: reusable);

        var persisted = await artifactRepository.WriteAsync(
            new ArtifactWriteRequest<CodeGenerationImpactReport>(
                WorkflowId: workflowId,
                Kind: "impact-analysis",
                SchemaVersion: 3,
                StageKey: targetStepKey ?? "all",
                Status: ArtifactStatus.Validated,
                Payload: report)
            {
                SessionId = workspace.SessionId.ToString()
            },
            cancellationToken).ConfigureAwait(false);

        return new CodeGenerationImpactProposal(persisted.Reference, persisted.Payload);
    }

    /// <summary>
    /// Persists the proposed impact, executes the approved restart, and records
    /// an applied plan only after Zhinu accepts the mutation.
    /// </summary>
    public async Task<CodeGenerationRestartApplicationResult> ApplyAsync(
        CodeGenerationRestartApprovalCommand approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.ApprovalId == Guid.Empty)
            throw new ArgumentException("A stable approval ID is required.", nameof(approval));
        var actor = (approvalActors ?? new RejectingAuthenticatedActorProvider())
            .GetRequiredActor();
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.SubjectId);

        var proposal = approval.Proposal;
        var workflowRunId = proposal.Impact.WorkflowRunId;
        var targetStepKey = proposal.Impact.TargetStepKey ??
            throw new RestartDecisionRejectedException(
                "MissingRestartTarget",
                "A restart approval requires a persisted target step.");
        var workflowId = workflowRunId.ToString("D");
        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            workflowId,
            cancellationToken).ConfigureAwait(false);
        var operationId = approval.ApprovalId;
        await using var decisionLease = await decisionLeases.AcquireAsync(
            workspace.SessionId,
            operationId,
            cancellationToken).ConfigureAwait(false);

        ArtifactEnvelope<CodeGenerationImpactReport> persisted;
        GuyabanoSession session;
        CodeGenerationImpactReport report;
        try
        {
            persisted = await artifactRepository.ReadAsync<CodeGenerationImpactReport>(
                    proposal.Artifact,
                    cancellationToken)
                .ConfigureAwait(false) ??
                throw new RestartDecisionRejectedException(
                    "PreviewNotFound",
                    $"Impact preview '{proposal.Artifact.ArtifactId}' no longer exists.");
            ValidatePersistedProposal(proposal, persisted, workflowRunId, targetStepKey);
            report = persisted.Payload;

            session = await sessionStore.GetAsync(workspace.SessionId, cancellationToken)
                .ConfigureAwait(false) ??
                throw new RestartDecisionRejectedException(
                    "SessionNotFound",
                    $"Session '{workspace.SessionId}' no longer exists.");
            if (!string.Equals(
                    session.CurrentWorkspaceRevision,
                    report.WorkspaceRevision,
                    StringComparison.Ordinal))
            {
                throw new RestartDecisionRejectedException(
                    "StaleWorkspaceRevision",
                    $"Impact preview '{report.PreviewId:D}' targets workspace revision " +
                    $"'{report.WorkspaceRevision ?? "uninitialized"}', but the accepted revision " +
                    $"is '{session.CurrentWorkspaceRevision ?? "uninitialized"}'.");
            }

            var currentPublication = await artifactRepository.ReadLatestAsync<
                    RepositoryReindexPublicationPayload>(
                    workflowId,
                    "repository-publication",
                    "post-generation",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    currentPublication?.Payload.IndexIdentity,
                    report.IndexIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    currentPublication?.Payload.WorkspaceRevisionId,
                    report.WorkspaceRevision,
                    StringComparison.Ordinal))
            {
                throw new RestartDecisionRejectedException(
                    "StaleHetuPublication",
                    $"Impact preview '{report.PreviewId:D}' references Hetu identity " +
                    $"'{report.IndexIdentity ?? "unavailable"}', but the current publication is " +
                    $"'{currentPublication?.Payload.IndexIdentity ?? "unavailable"}'.");
            }
        }
        catch (RestartDecisionRejectedException exception)
        {
            var recoveryResult = await RecordDecisionRejectionAsync(
                approval,
                workspace.SessionId,
                exception,
                cancellationToken).ConfigureAwait(false);
            exception.AttachRecovery(recoveryResult);
            throw;
        }

        var outcome = await restartService.RestartAsync(
            new RestartApproval(
                operationId,
                Guid.CreateVersion7(),
                report.PreviewId,
                workflowRunId,
                targetStepKey,
                report.WorkspaceRevision,
                report.IndexIdentity,
                report.ChangeSetHash,
                actor.SubjectId,
                Approved: true,
                ApprovedAt: approval.ApprovedAt),
            cancellationToken).ConfigureAwait(false);
        if (!outcome.Applied)
            return new CodeGenerationRestartApplicationResult(report, outcome, null);

        var plan = new CodeGenerationAppliedRestartPlan(
            ApprovalId: operationId,
            PreviewId: report.PreviewId,
            WorkflowRunId: workflowRunId,
            TargetStepKey: targetStepKey,
            WorkspaceRevision: report.WorkspaceRevision,
            IndexIdentity: report.IndexIdentity,
            ChangeSetHash: report.ChangeSetHash,
            ApprovedBy: actor.SubjectId,
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
                Payload: plan,
                Inputs: [proposal.Artifact])
            {
                SessionId = workspace.SessionId.ToString()
            },
            cancellationToken).ConfigureAwait(false);
        return new CodeGenerationRestartApplicationResult(report, outcome, plan);
    }

    private async Task<SessionRecoveryExecutionResult> RecordDecisionRejectionAsync(
        CodeGenerationRestartApprovalCommand approval,
        GuyabanoSessionId sessionId,
        RestartDecisionRejectedException exception,
        CancellationToken cancellationToken)
    {
        var report = approval.Proposal.Impact;
        var references = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["approvalId"] = approval.ApprovalId.ToString("D"),
            ["previewId"] = report.PreviewId.ToString("D"),
            ["previewArtifactId"] = approval.Proposal.Artifact.ArtifactId,
            ["workflowRunId"] = report.WorkflowRunId.ToString("D"),
            ["targetStepKey"] = report.TargetStepKey ?? "missing",
            ["reasonCode"] = exception.ReasonCode,
            ["workspaceRevision"] = report.WorkspaceRevision ?? "uninitialized",
            ["indexIdentity"] = report.IndexIdentity ?? "unavailable"
        };
        var incident = new SessionIncident(
            approval.ApprovalId,
            sessionId,
            exception.ReasonCode,
            SessionIncidentSeverity.Warning,
            exception.Message,
            approval.ApprovedAt,
            report.WorkflowRunId,
            references);
        var detected = await recovery.DetectAsync(incident, cancellationToken)
            .ConfigureAwait(false);
        var plan = new SessionRecoveryPlan(
            approval.ApprovalId,
            incident.IncidentId,
            sessionId,
            SessionRecoveryAction.RefreshPreview,
            exception.Message,
            report.WorkspaceRevision,
            Automatic: true,
            approval.ApprovedAt,
            report.WorkflowRunId,
            references);
        var planned = await recovery.PlanAsync(plan, detected.EventId, cancellationToken)
            .ConfigureAwait(false);
        return await recovery.ExecuteAsync(
            plan,
            planned.EventId,
            attempt: 1,
            async (_, ct) =>
            {
                var replacement = await ProposeAsync(
                    report.WorkflowRunId,
                    report.TargetStepKey,
                    ct).ConfigureAwait(false);
                var fresh = replacement.Impact;
                var verified = fresh.PreviewId != report.PreviewId &&
                    fresh.WorkflowRunId == report.WorkflowRunId &&
                    string.Equals(fresh.TargetStepKey, report.TargetStepKey, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(replacement.Artifact.ArtifactId);
                return new SessionRecoveryActionReceipt(
                    Guid.CreateVersion7(),
                    SessionRecoveryAction.RefreshPreview,
                    "impact-preview",
                    fresh.PreviewId.ToString("D"),
                    $"Replacement impact preview '{fresh.PreviewId:D}' was persisted as artifact " +
                        $"'{replacement.Artifact.ArtifactId}' and requires a new approval.",
                    fresh.GeneratedAt,
                    verified,
                    new Dictionary<string, string>(references, StringComparer.Ordinal)
                    {
                        ["replacementPreviewId"] = fresh.PreviewId.ToString("D"),
                        ["replacementPreviewArtifactId"] = replacement.Artifact.ArtifactId,
                        ["replacementChangeSetHash"] = fresh.ChangeSetHash
                    });
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePersistedProposal(
        CodeGenerationImpactProposal proposal,
        ArtifactEnvelope<CodeGenerationImpactReport> persisted,
        Guid workflowRunId,
        string targetStepKey)
    {
        var report = persisted.Payload;
        var supplied = proposal.Impact;
        var persistedHash = ComputeChangeSetHash(
            report.TargetStepKey,
            report.WorkspaceRevision,
            report.IndexIdentity,
            report.ImpactedNodes,
            report.ReusableStepKeys);
        var suppliedHash = ComputeChangeSetHash(
            supplied.TargetStepKey,
            supplied.WorkspaceRevision,
            supplied.IndexIdentity,
            supplied.ImpactedNodes,
            supplied.ReusableStepKeys);
        if (persisted.WorkflowId != workflowRunId.ToString("D") ||
            persisted.StageKey != targetStepKey ||
            report.PreviewId != supplied.PreviewId ||
            report.WorkflowRunId != workflowRunId ||
            report.TargetStepKey != targetStepKey ||
            report.RestartMode != StepRestartMode.Dependents ||
            supplied.RestartMode != report.RestartMode ||
            !string.Equals(report.WorkspaceRevision, supplied.WorkspaceRevision, StringComparison.Ordinal) ||
            !string.Equals(report.IndexIdentity, supplied.IndexIdentity, StringComparison.Ordinal) ||
            !string.Equals(report.ChangeSetHash, persistedHash, StringComparison.Ordinal) ||
            !string.Equals(supplied.ChangeSetHash, report.ChangeSetHash, StringComparison.Ordinal) ||
            !string.Equals(suppliedHash, persistedHash, StringComparison.Ordinal))
        {
            throw new RestartDecisionRejectedException(
                "PreviewMismatch",
                $"Approval does not match persisted impact preview '{report.PreviewId:D}'.");
        }
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

    private static string ComputeChangeSetHash(
        string? targetStepKey,
        string? workspaceRevision,
        string? indexIdentity,
        IReadOnlyList<CodeGenerationImpactNode> nodes,
        IReadOnlyList<string> reusableStepKeys)
    {
        var components = new List<string>
        {
            $"target={targetStepKey}",
            $"workspace={workspaceRevision}",
            $"index={indexIdentity}"
        };
        components.AddRange(nodes
            .OrderBy(item => item.StepKey, StringComparer.Ordinal)
            .ThenBy(item => item.Cause)
            .ThenBy(item => item.TaskId, StringComparer.Ordinal)
            .ThenBy(item => item.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.HetuNodeId, StringComparer.Ordinal)
            .Select(item =>
                $"impact={item.StepKey}|{item.TaskId}|{item.Cause}|{item.FilePath}|{item.HetuNodeId}|{item.Reason}"));
        components.AddRange(reusableStepKeys
            .OrderBy(item => item, StringComparer.Ordinal)
            .Select(item => $"reuse={item}"));
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", components))));
    }
}
