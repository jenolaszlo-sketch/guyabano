using System.Text.Json;
using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Penghou.Cangjie;
using Penghou.Cangjie.Sqlite;
using Penghou.Hetu;
using Penghou.Hetu.CSharp;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

public sealed class ImpactAnalysisTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-impact-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Analyze_DistinguishesWorkflowArtifactAndCodeGraphCauses()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionStore = new FileSystemGuyabanoSessionStore(Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: ct);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);
        var projectPath = Path.Combine(workspace.HostPath, "Sample.csproj");
        var aPath = Path.Combine(workspace.HostPath, "A.cs");
        var bPath = Path.Combine(workspace.HostPath, "B.cs");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", ct);
        await File.WriteAllTextAsync(aPath, "namespace Sample; public class A { public int Compute() => 1; }", ct);
        await File.WriteAllTextAsync(bPath, "namespace Sample; public class B { public int Run() { var a = new A(); return a.Compute(); } }", ct);

        await using var hetu = new HetuHostBuilder().AddCSharpPlugin().Build();
        var contextStore = new SqliteContextStore(new CangjieSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "cangjie.db"),
            Pooling = false
        });
        var inner = new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts"));
        var indexing = new ContextIndexingArtifactRepository(inner, contextStore);
        var artifacts = new ZhinuPublishingArtifactRepository(indexing, resolver);

        var options = Options.Create(new CodeGenerationWorkerOptions
        {
            OutputRoot = rootPath,
            RepositoryContextEnabled = true,
            RepositoryId = "repo:test"
        });

        // Snapshot of the workspace per task's logical writes
        var s0 = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace.HostPath, ct);
        var empty = new Dictionary<string, (string, long)>();
        var onlyA = new Dictionary<string, (string, long)> { { "A.cs", s0["A.cs"] } };
        var onlyB = new Dictionary<string, (string, long)> { { "B.cs", s0["B.cs"] } };

        // Publish manifests: task-a owns A.cs (Created), task-b owns B.cs (Created)
        var manifestA = await CreateManifestAsync(
            session, runId, workspace, "generation/task-a/leaf-a", "leaf-a", "task-a",
            empty, onlyA, [], artifacts, ct);
        var manifestB = await CreateManifestAsync(
            session, runId, workspace, "generation/task-b/leaf-b", "leaf-b", "task-b",
            new Dictionary<string, (string, long)> { { "A.cs", s0["A.cs"] } }, s0, [], artifacts, ct);

        // Baseline reindex of the original workspace
        var decisionLeases = new FileSystemSessionDecisionLeaseProvider(
            Path.Combine(rootPath, ".gen", "decision-locks"));
        var reindexer = new CodeGenerationRepositoryReindexer(
            hetu, contextStore, indexing, resolver, decisionLeases, options);
        RepositoryReindexReceipt baseline;
        using (CodeGenerationZhinuStepScope.Push(new Penghou.Zhinu.WorkflowStepContext(runId, Guid.NewGuid(), "repository/reindex-post-generation", 1, 0, false)))
            baseline = await reindexer.ReindexAsync(new RepositoryReindexRequest(runId.ToString("D")), ct);
        baseline.FilesNew.Should().Be(3);

        // Mutate A.cs: new manifest for task-a shows Modified
        await File.WriteAllTextAsync(aPath, "namespace Sample; public class A { public int Compute() => 2; }", ct);
        var s1 = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace.HostPath, ct);
        var beforeA = new Dictionary<string, (string, long)> { { "A.cs", s0["A.cs"] } };
        var afterA = new Dictionary<string, (string, long)> { { "A.cs", s1["A.cs"] } };
        var manifestA2 = await CreateManifestAsync(
            session, runId, workspace, "generation/task-a/leaf-a", "leaf-a", "task-a",
            beforeA, afterA, [], artifacts, ct);

        // Post-generation reindex picks up the change
        RepositoryReindexReceipt receipt;
        using (CodeGenerationZhinuStepScope.Push(new Penghou.Zhinu.WorkflowStepContext(runId, Guid.NewGuid(), "repository/reindex-post-generation", 1, 1, false)))
            receipt = await reindexer.ReindexAsync(new RepositoryReindexRequest(runId.ToString("D")), ct);
        receipt.FilesChanged.Should().Be(1);

        // Build the restart service + impact service
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"), Pooling = false });
        var workflow = new BranchedManifestWorkflow();
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("impact-branched", "1", workflow));
        await engine.StartAsync("impact-branched", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        await using var sessionEvents = new SimingSessionEventStore(
            Path.Combine(rootPath, ".gen", "session-events"));
        var restartService = new CodeGenerationWorkflowRestartService(
            engine,
            sessionStore,
            sessionEvents,
            NullLogger<CodeGenerationWorkflowRestartService>.Instance);
        var impactService = new CodeGenerationImpactAnalysisService(
            hetu, contextStore, artifacts, restartService, resolver,
            sessionStore, decisionLeases,
            new SessionRecoveryCoordinator(sessionEvents), options,
            new TestApprovalActorProvider());

        var report = await impactService.AnalyzeAsync(runId, "branch-a", ct);
        report.IndexIdentity.Should().Be(receipt.IndexIdentity);

        // branch-a is the restart target: Workflow cause (its own invalidated by target)
        report.ImpactedNodes.Should().Contain(n => n.StepKey == "branch-a" && n.Cause == CodeGenerationImpactCause.Workflow);
        // a-child depends on branch-a: Workflow cause too
        report.ImpactedNodes.Should().Contain(n => n.StepKey == "a-child" && n.Cause == CodeGenerationImpactCause.Workflow);

        // impact-analysis artifact persisted
        var persisted = await artifacts.ReadLatestAsync<CodeGenerationImpactReport>(runId.ToString("D"), "impact-analysis", "branch-a", ct);
        persisted.Should().NotBeNull();
        persisted!.Payload.IndexIdentity.Should().Be(receipt.IndexIdentity);
        persisted.Payload.InvalidatedStepKeys.Should().NotBeEmpty();

        // Code-graph cause: task-b's step owns B.cs which is a dependent of changed A.cs symbol
        report.ImpactedNodes.Should().Contain(n => n.TaskId == "leaf-b" && n.Cause == CodeGenerationImpactCause.CodeGraph);
        // Artifact cause: task-a owns the modified A.cs file
        report.ImpactedNodes.Should().Contain(n => n.TaskId == "leaf-a" && n.Cause == CodeGenerationImpactCause.Artifact);
        // Workflow cause: a-child depends on branch-a (restart target)
        report.ImpactedNodes.Should().Contain(n => n.StepKey == "a-child" && n.Cause == CodeGenerationImpactCause.Workflow);
        // Reusable steps keep workflow-siblings that are unaffected by code-graph impact
        report.ReusableStepKeys.Should().Contain("branch-b");
        report.ReusableStepKeys.Should().Contain("b-child");
    }

    [Fact]
    public async Task Apply_PersistsAppliedRestartPlan_AndRequiresApprovalAudit()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionStore = new FileSystemGuyabanoSessionStore(Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:apply", "workspace:test", cancellationToken: ct);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);
        await File.WriteAllTextAsync(Path.Combine(workspace.HostPath, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", ct);
        await File.WriteAllTextAsync(Path.Combine(workspace.HostPath, "A.cs"), "namespace Sample; public class A { }", ct);

        await using var hetu = new HetuHostBuilder().AddCSharpPlugin().Build();
        var contextStore = new SqliteContextStore(new CangjieSqliteOptions { DatabasePath = Path.Combine(rootPath, ".gen", "cangjie.db"), Pooling = false });
        var inner = new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts"));
        var indexing = new ContextIndexingArtifactRepository(inner, contextStore);
        var artifacts = new ZhinuPublishingArtifactRepository(indexing, resolver);
        var options = Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, RepositoryContextEnabled = true, RepositoryId = "repo:apply" });

        // Publish a manifest for task-a, reindex, then run a branched workflow
        var s0 = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace.HostPath, ct);
        var onlyA = new Dictionary<string, (string, long)> { { "A.cs", s0["A.cs"] } };
        await CreateManifestAsync(session, runId, workspace, "generation/task-a/leaf-a", "leaf-a", "task-a",
            new Dictionary<string, (string, long)>(), onlyA, [], artifacts, ct);
        var decisionLeases = new FileSystemSessionDecisionLeaseProvider(
            Path.Combine(rootPath, ".gen", "decision-locks"));
        var reindexer = new CodeGenerationRepositoryReindexer(
            hetu, contextStore, indexing, resolver, decisionLeases, options);
        await reindexer.ReindexAsync(new RepositoryReindexRequest(runId.ToString("D")), ct);
        var publication = await artifacts.ReadLatestAsync<RepositoryReindexPublicationPayload>(
            runId.ToString("D"), "repository-publication", "post-generation", ct);
        publication.Should().NotBeNull();
        (await sessionStore.UpdateWorkspaceRevisionAsync(
            session.Id,
            expectedRevision: null,
            publication!.Payload.WorkspaceRevisionId ??
                throw new InvalidOperationException("Publication must bind a workspace revision."),
            ct)).Should().NotBeNull();

        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"), Pooling = false });
        var workflow = new BranchedManifestWorkflow();
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("apply-branched", "1", workflow));
        await engine.StartAsync("apply-branched", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        await using var sessionEvents = new SimingSessionEventStore(
            Path.Combine(rootPath, ".gen", "session-events"));
        var restartService = new CodeGenerationWorkflowRestartService(
            engine,
            sessionStore,
            sessionEvents,
            NullLogger<CodeGenerationWorkflowRestartService>.Instance);
        var impactService = new CodeGenerationImpactAnalysisService(
            hetu, contextStore, artifacts, restartService, resolver,
            sessionStore, decisionLeases,
            new SessionRecoveryCoordinator(sessionEvents), options,
            new TestApprovalActorProvider());

        var proposal = await impactService.ProposeAsync(runId, "branch-a", ct);
        var tampered = proposal with
        {
            Impact = proposal.Impact with { ChangeSetHash = new string('0', 64) }
        };
        var rejectTampered = async () => await impactService.ApplyAsync(
            new CodeGenerationRestartApprovalCommand(
                Guid.CreateVersion7(), tampered, DateTimeOffset.UtcNow),
            ct);
        var rejection = await rejectTampered.Should()
            .ThrowAsync<RestartDecisionRejectedException>();
        rejection.Which.ReasonCode.Should().Be("PreviewMismatch");
        rejection.Which.RecoveryOutcome.Should().Be(SessionRecoveryOutcome.Recovered);
        rejection.Which.ReplacementPreviewId.Should().NotBeNull();

        var originalPublication = publication.Payload;
        await artifacts.WriteAsync(
            new ArtifactWriteRequest<RepositoryReindexPublicationPayload>(
                runId.ToString("D"),
                "repository-publication",
                2,
                "post-generation",
                ArtifactStatus.Validated,
                originalPublication with
                {
                    IndexRunId = $"{originalPublication.IndexRunId}:new",
                    IndexIdentity = $"{originalPublication.IndexIdentity}:new",
                    PublishedAt = DateTimeOffset.UtcNow
                })
            {
                SessionId = session.Id.ToString()
            },
            ct);
        var rejectStaleGraph = async () => await impactService.ApplyAsync(
            new CodeGenerationRestartApprovalCommand(
                Guid.CreateVersion7(), proposal, DateTimeOffset.UtcNow),
            ct);
        var staleGraph = await rejectStaleGraph.Should()
            .ThrowAsync<RestartDecisionRejectedException>();
        staleGraph.Which.ReasonCode.Should().Be("StaleHetuPublication");
        staleGraph.Which.RecoveryOutcome.Should().Be(SessionRecoveryOutcome.Recovered);
        staleGraph.Which.ReplacementPreviewId.Should().NotBeNull();
        var rejectionHistory = await sessionEvents.ReadAsync(
            session.Id,
            cancellationToken: ct);
        rejectionHistory
            .Where(item => item.EventType == SessionEventTypes.IncidentDetected)
            .Select(item => item.CrossSystemRefs?.GetValueOrDefault("reasonCode"))
            .Should().Contain(["PreviewMismatch", "StaleHetuPublication"]);
        rejectionHistory.Count(item =>
                item.EventType == SessionEventTypes.RecoverySucceeded)
            .Should().Be(2);
        rejectionHistory.Count(item =>
                item.EventType == SessionEventTypes.RecoveryAttempted)
            .Should().Be(2);
        SessionTimelineProjection.Project(rejectionHistory).OperatorState.Should()
            .Be(SessionOperatorState.AwaitingApproval);

        await artifacts.WriteAsync(
            new ArtifactWriteRequest<RepositoryReindexPublicationPayload>(
                runId.ToString("D"),
                "repository-publication",
                2,
                "post-generation",
                ArtifactStatus.Validated,
                originalPublication with
                {
                    IndexRunId = $"{originalPublication.IndexRunId}:restored",
                    PublishedAt = DateTimeOffset.UtcNow
                })
            {
                SessionId = session.Id.ToString()
            },
            ct);

        var application = await impactService.ApplyAsync(
            new CodeGenerationRestartApprovalCommand(
                Guid.CreateVersion7(), proposal, DateTimeOffset.UtcNow),
            ct);

        var plan = await artifacts.ReadLatestAsync<CodeGenerationAppliedRestartPlan>(runId.ToString("D"), "applied-restart-plan", "branch-a", ct);
        application.Outcome.Applied.Should().BeTrue();
        application.AppliedPlan.Should().NotBeNull();
        plan.Should().NotBeNull();
        plan!.Payload.ApprovedBy.Should().Be("tester");
        plan.Payload.ApprovalId.Should().Be(application.AppliedPlan!.ApprovalId);
        plan.Payload.PreviewId.Should().Be(application.Impact.PreviewId);
        plan.Payload.WorkspaceRevision.Should().Be(application.Impact.WorkspaceRevision);
        plan.Payload.IndexIdentity.Should().Be(application.Impact.IndexIdentity);
        plan.Payload.ChangeSetHash.Should().Be(application.Impact.ChangeSetHash);
        plan.Payload.InvalidatedStepKeys.Should().Contain("branch-a");
        plan.Payload.RerunStepKeys.Should().Contain("branch-a");
        plan.Payload.ReusableStepKeys.Should().Contain("branch-b");

        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var steps = await engine.GetStepsAsync(runId, ct);
        steps.Should().Contain(s => s.StepKey == "branch-a");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private static async Task<ArtifactEnvelope<GeneratedFileManifest>> CreateManifestAsync(
        GuyabanoSession session,
        Guid runId,
        CodeGenerationWorkspace workspace,
        string stepKey,
        string taskId,
        string parentTaskId,
        IReadOnlyDictionary<string, (string Hash, long Length)> before,
        IReadOnlyDictionary<string, (string Hash, long Length)> after,
        IReadOnlyList<string> skippedFiles,
        IArtifactRepository artifacts,
        CancellationToken ct)
    {
        var previous = await artifacts.ReadLatestAsync<GeneratedFileManifest>(runId.ToString("D"), "generated-file-manifest", taskId, ct);
        var manifest = await GeneratedFileManifestFactory.CreateWithWorkspaceDiffAsync(
            sessionId: session.Id.ToString(),
            workflowRunId: runId.ToString("D"),
            stepKey: stepKey,
            stepRevision: 1,
            workspaceHostPath: workspace.HostPath,
            workspaceCiPath: workspace.CiRelativePath,
            taskId: taskId,
            beforeSnapshot: before,
            afterSnapshot: after,
            previousManifest: previous?.Payload,
            skippedFiles: skippedFiles,
            parentTaskId: parentTaskId,
            model: "test-model",
            cancellationToken: ct);
        return await artifacts.WriteAsync(
            new ArtifactWriteRequest<GeneratedFileManifest>(
                runId.ToString("D"),
                "generated-file-manifest",
                2,
                taskId,
                ArtifactStatus.Validated,
                manifest),
            ct);
    }

    private sealed class BranchedManifestWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.StepAsync("root", input, (value, step, token) => Task.FromResult("root"), new StepOptions(), cancellationToken);
            var a = context.StepAsync("branch-a", input, (value, step, token) => Task.FromResult("a"), new StepOptions { DependsOn = ["root"] }, cancellationToken);
            var b = context.StepAsync("branch-b", input, (value, step, token) => Task.FromResult("b"), new StepOptions { DependsOn = ["root"] }, cancellationToken);
            await Task.WhenAll(a, b);
            await context.StepAsync("a-child", input, (value, step, token) => Task.FromResult("a-child"), new StepOptions { DependsOn = ["branch-a"] }, cancellationToken);
            await context.StepAsync("b-child", input, (value, step, token) => Task.FromResult("b-child"), new StepOptions { DependsOn = ["branch-b"] }, cancellationToken);
            return input;
        }
    }
}
