using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Session;
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

public sealed class CanonicalSessionScenarioTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-canonical-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Canonical_GenerateClarifyPreviewApproveRerunPromoteReindexAudit()
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
        var inner = new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen"));
        var indexing = new ContextIndexingArtifactRepository(inner, contextStore);
        var artifacts = new ZhinuPublishingArtifactRepository(indexing, resolver);
        var sessionEvents = new FileSystemSessionEventStore(Path.Combine(rootPath, ".gen", "session-events"));
        var options = Options.Create(new CodeGenerationWorkerOptions
        {
            OutputRoot = rootPath,
            RepositoryContextEnabled = true,
            RepositoryId = "repo:test"
        });

        // 1. Generate successfully: publish ownership manifests + accept initial workspace revision
        var s0 = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace.HostPath, ct);
        await CreateManifestAsync(session, runId, workspace, "generation/task-a/leaf-a", "leaf-a", "task-a",
            new Dictionary<string, (string, long)>(), new Dictionary<string, (string, long)> { { "A.cs", s0["A.cs"] } }, artifacts, ct);
        await CreateManifestAsync(session, runId, workspace, "generation/task-b/leaf-b", "leaf-b", "task-b",
            new Dictionary<string, (string, long)> { { "A.cs", s0["A.cs"] } }, s0, artifacts, ct);
        var workspaceHash0 = await ComputeRevisionAsync(workspace.HostPath, ct);
        await sessionStore.UpdateWorkspaceRevisionAsync(session.Id, null, workspaceHash0, ct);

        // Baseline reindex + run the branched workflow
        var reindexer = new CodeGenerationRepositoryReindexer(hetu, contextStore, indexing, resolver, options);
        using (CodeGenerationZhinuStepScope.Push(new Penghou.Zhinu.WorkflowStepContext(runId, Guid.NewGuid(), "repository/reindex-post-generation", 1, 0, false)))
            await reindexer.ReindexAsync(new RepositoryReindexRequest(runId.ToString("D")), ct);

        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"), Pooling = false });
        var workflow = new BranchedManifestWorkflow();
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("canonical", "1", workflow));
        await engine.StartAsync("canonical", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        // Session events for the run start
        await sessionEvents.AppendAsync(new SessionEventRequest(session.Id, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow, CorrelationId: runId), ct);
        await sessionEvents.AppendAsync(new SessionEventRequest(session.Id, "guyabano", SessionEventTypes.WorkflowStarted, DateTimeOffset.UtcNow, CorrelationId: runId), ct);

        // 2. Add a clarification, deliberately promoted into Cangjie knowledge
        var clarification = new SessionClarificationService(
            new CangjieRevisionedConceptService(contextStore), sessionStore, sessionEvents);
        var knowledge = await clarification.PromoteAsync(
            session.Id.Value, "clarify:payment", "Use decimal for money amounts; never float.", runId, ct);
        knowledge.Kind.Should().Be(ContextKinds.Knowledge);

        // 3. Preview the cascade, 4. approve, 5. rerun only affected, reuse siblings
        var restartService = new CodeGenerationWorkflowRestartService(engine, sessionStore, sessionEvents, NullLogger<CodeGenerationWorkflowRestartService>.Instance);
        var impactService = new CodeGenerationImpactAnalysisService(hetu, contextStore, artifacts, restartService, resolver, options);
        var preview = await impactService.AnalyzeAsync(runId, "branch-a", ct);
        preview.ImpactedNodes.Should().Contain(n => n.StepKey == "branch-a" && n.Cause == CodeGenerationImpactCause.Workflow);
        preview.ImpactedNodes.Should().Contain(n => n.StepKey == "a-child" && n.Cause == CodeGenerationImpactCause.Workflow);

        var beforeSteps = await engine.GetStepsAsync(runId, ct);
        var branchBRevisionBefore = beforeSteps.Single(s => s.StepKey == "branch-b").Revision;

        await impactService.ApplyAsync(runId, "branch-a", "tester", ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var afterSteps = await engine.GetStepsAsync(runId, ct);
        afterSteps.Where(s => s.StepKey == "branch-a").Max(s => s.Revision).Should().BeGreaterThan(
            beforeSteps.Single(s => s.StepKey == "branch-a").Revision);
        afterSteps.Single(s => s.StepKey == "branch-b").Revision.Should().Be(branchBRevisionBefore);

        // 6. Validate staging and promote the accepted change
        var stagingService = new CodeGenerationStagingService(resolver, sessionStore, artifacts, sessionEvents, options);
        var mutation = await stagingService.CreateStagingAsync(session.Id.Value, "canonical-mutation", ct);
        await File.WriteAllTextAsync(Path.Combine(mutation.StagingHostPath, "A.cs"), "namespace Sample; public class A { public int Compute() => 2; }", ct);
        var promotion = await stagingService.ValidateAndPromoteAsync(
            session.Id.Value, "canonical-mutation", workspaceHash0,
            (path, token) => Task.FromResult(new StagingValidationResult(true)), ct);
        (await sessionStore.GetAsync(session.Id, ct))!.CurrentWorkspaceRevision.Should().Be(promotion.ToRevision);

        // 7. Reindex the promoted workspace
        using (CodeGenerationZhinuStepScope.Push(new Penghou.Zhinu.WorkflowStepContext(runId, Guid.NewGuid(), "repository/reindex-post-generation", 1, 1, false)))
            await reindexer.ReindexAsync(new RepositoryReindexRequest(runId.ToString("D")), ct);

        // 8. Reconstruct the audit timeline
        var auditService = new SessionConsistencyAuditService(
            engine, sessionStore, sessionEvents, contextStore, artifacts, resolver, options);
        var audit = await auditService.AuditAsync(session.Id.Value, ct);
        audit.WorkflowRunsChecked.Should().Be(1);
        audit.Findings.Should().NotContain(f => f.Severity == SessionAuditSeverity.Error);

        var events = await sessionEvents.ReadAsync(session.Id, cancellationToken: ct);
        var timeline = SessionTimelineProjection.RenderTimeline(events);
        timeline.Should().Contain(line => line.Contains("user-message by user"));
        timeline.Should().Contain(line => line.Contains("workflow-started by guyabano"));
        timeline.Should().Contain(line => line.Contains("clarification-promoted by guyabano") && line.Contains("knowledgeKey=clarify:payment"));
        timeline.Should().Contain(line => line.Contains("approval-granted by tester"));
        timeline.Should().Contain(line => line.Contains("restart-applied by guyabano"));
        timeline.Should().Contain(line => line.Contains("workspace-promoted by guyabano") && line.Contains("toRevision=" + promotion.ToRevision));
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
        IArtifactRepository artifacts,
        CancellationToken ct)
    {
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
            previousManifest: null,
            skippedFiles: [],
            parentTaskId: parentTaskId,
            model: "test-model",
            cancellationToken: ct);
        return await artifacts.WriteAsync(
            new ArtifactWriteRequest<GeneratedFileManifest>(
                runId.ToString("D"), "generated-file-manifest", 2, taskId, ArtifactStatus.Validated, manifest),
            ct);
    }

    private static async Task<string> ComputeRevisionAsync(string path, CancellationToken ct)
    {
        var snapshot = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(path, ct);
        var ordered = snapshot
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value.Hash}");
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(string.Join("|", ordered))))
            .ToLowerInvariant();
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
