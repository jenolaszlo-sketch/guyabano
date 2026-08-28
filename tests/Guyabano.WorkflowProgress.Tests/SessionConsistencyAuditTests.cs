using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Penghou.Cangjie;
using Penghou.Cangjie.Sqlite;
using Penghou.Hetu;
using Penghou.Hetu.CSharp;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

public sealed class SessionConsistencyAuditTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-audit-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Audit_ConsistentSession_IsConsistent()
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
        await File.WriteAllTextAsync(Path.Combine(workspace.HostPath, "A.cs"), "class A {}", ct);
        await File.WriteAllTextAsync(Path.Combine(workspace.HostPath, "B.cs"), "class B {}", ct);
        var workspaceHash = await ComputeRevisionAsync(workspace.HostPath, ct);
        await sessionStore.UpdateWorkspaceRevisionAsync(session.Id, null, workspaceHash, ct);

        await using var hetu = new HetuHostBuilder().AddCSharpPlugin().Build();
        var contextStore = new SqliteContextStore(new CangjieSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "cangjie.db"),
            Pooling = false
        });
        var inner = new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen"));
        var indexing = new ContextIndexingArtifactRepository(inner, contextStore);
        var artifacts = new ZhinuPublishingArtifactRepository(indexing, resolver);
        using var sessionEvents = new SimingSessionEventStore(Path.Combine(rootPath, ".gen", "session-events"));

        // Real engine run publishes validation-evidence, baize-execution, and repository-publication
        var workflow = new PublishEvidenceWorkflow(artifacts, workspaceHash, session.Id);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("audit-pipeline", "1", workflow));
        await engine.StartAsync("audit-pipeline", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var auditService = new SessionConsistencyAuditService(
            engine, sessionStore, sessionEvents, contextStore, artifacts, resolver);

        var report = await auditService.AuditAsync(session.Id.Value, ct);
        report.WorkflowRunsChecked.Should().Be(1);
        report.ArtifactsResolved.Should().Be(3);
        report.IsConsistent.Should().BeTrue(
            report.Findings.Count == 0
                ? "no findings"
                : string.Join(" | ", report.Findings.Select(f => $"{f.Category}:{f.Message}")));
    }

    [Fact]
    public async Task Audit_DetectsMissingAuthoritativeArtifactFile()
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
        await File.WriteAllTextAsync(Path.Combine(workspace.HostPath, "A.cs"), "class A {}", ct);
        var workspaceHash = await ComputeRevisionAsync(workspace.HostPath, ct);
        await sessionStore.UpdateWorkspaceRevisionAsync(session.Id, null, workspaceHash, ct);

        await using var hetu = new HetuHostBuilder().AddCSharpPlugin().Build();
        var contextStore = new SqliteContextStore(new CangjieSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "cangjie.db"),
            Pooling = false
        });
        var inner = new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen"));
        var indexing = new ContextIndexingArtifactRepository(inner, contextStore);
        var artifacts = new ZhinuPublishingArtifactRepository(indexing, resolver);
        using var sessionEvents = new SimingSessionEventStore(Path.Combine(rootPath, ".gen", "session-events"));

        var workflow = new PublishEvidenceWorkflow(artifacts, workspaceHash, session.Id);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("audit-broken", "1", workflow));
        await engine.StartAsync("audit-broken", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        // Delete the validation-evidence authoritative file to simulate incomplete publication
        var published = await engine.GetArtifactsAsync(runId, ct);
        var evidence = published.Single(a => a.ArtifactType == "validation-evidence");
        File.Delete(Path.Combine(rootPath, ".gen", evidence.Location));

        var auditService = new SessionConsistencyAuditService(
            engine, sessionStore, sessionEvents, contextStore, artifacts, resolver);
        var report = await auditService.AuditAsync(session.Id.Value, ct);

        report.ArtifactsResolved.Should().Be(2);
        report.Findings.Should().Contain(f =>
            f.Severity == SessionAuditSeverity.Error &&
            f.Category == "zhinu" &&
            f.Message.Contains("validation-evidence"));
        report.IsConsistent.Should().BeFalse();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
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

    private sealed class PublishEvidenceWorkflow(
        IArtifactRepository artifacts,
        string workspaceRevision,
        GuyabanoSessionId sessionId) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.StepAsync("build-1", input, async (value, step, token) =>
            {
                using var scope = CodeGenerationZhinuStepScope.Push(step);
                var evidence = new ValidationEvidencePayload(
                    BuildResult: new CodeGenerationBuildResult(true, 0, null, []),
                    SessionId: sessionId.ToString(),
                    WorkflowRunId: context.WorkflowRunId.ToString("D"),
                    StepKey: step.StepKey,
                    StepRevision: step.Revision,
                    WorkspaceHostPath: ".",
                    WorkspaceCiPath: ".",
                    EvaluatedFiles: ["A.cs"],
                    PublishedAt: DateTimeOffset.UtcNow,
                    WorkspaceRevisionId: workspaceRevision);
                await artifacts.WriteAsync(
                    new ArtifactWriteRequest<ValidationEvidencePayload>(
                        context.WorkflowRunId.ToString("D"), "validation-evidence", 1, "build-1", ArtifactStatus.Validated, evidence),
                    token);
                return value;
            }, cancellationToken: cancellationToken);

            await context.StepAsync("model-1", input, async (value, step, token) =>
            {
                using var scope = CodeGenerationZhinuStepScope.Push(step);
                var record = new BaizeExecutionRecord(
                    SessionId: sessionId.ToString(),
                    WorkflowRunId: context.WorkflowRunId.ToString("D"),
                    WorkflowStepKey: step.StepKey,
                    WorkflowStepRevision: step.Revision,
                    CangjieSnapshotId: null, CangjieStrategy: null, CangjieStrategyVersion: null,
                    CangjieQueryIdentity: null, HetuIndexRunId: null, HetuIndexIdentity: null,
                    WorkspaceRevision: workspaceRevision,
                    Purpose: "code-generation",
                    RequestedModel: "test-model",
                    Provider: "test", ActualModel: "test-model", ApiStyle: "test",
                    RouterAttempts: [], PromptTokens: 10, CompletionTokens: 5, TotalTokens: 15,
                    PromptCacheHitTokens: 0, PromptCacheMissTokens: 15,
                    TotalDurationMilliseconds: 100, LoadDurationMilliseconds: null,
                    PromptEvaluationDurationMilliseconds: null, GenerationDurationMilliseconds: null,
                    GenerationTokensPerSecond: null, NativeToolCallCount: null,
                    FinishReason: "stop", FinishReasonKind: "Stop",
                    ContentWasRepaired: false, ContentRepairAttemptCount: 0,
                    RateLimit: null, ResponseId: null,
                    RequestHash: "req", ResponseHash: "resp",
                    Succeeded: true,
                    StartedAt: DateTimeOffset.UtcNow, CompletedAt: DateTimeOffset.UtcNow, Error: null);
                await artifacts.WriteAsync(
                    new ArtifactWriteRequest<BaizeExecutionRecord>(
                        context.WorkflowRunId.ToString("D"), "baize-execution", 1, "code-generation/build-1", ArtifactStatus.Validated, record),
                    token);
                return value;
            }, cancellationToken: cancellationToken);

            await context.StepAsync("repository/reindex-post-generation", input, async (value, step, token) =>
            {
                using var scope = CodeGenerationZhinuStepScope.Push(step);
                var publication = new RepositoryReindexPublicationPayload(
                    RepositoryId: "repo:test", Location: ".", IndexRunId: "run-1", IndexIdentity: workspaceRevision,
                    ProviderSnapshotIdentity: null, IsConsistentSnapshot: true,
                    FilesDiscovered: 3, FilesNew: 3, FilesChanged: 0, FilesUnchanged: 0, FilesDeleted: 0, NodesProduced: 8,
                    SessionId: sessionId.ToString(),
                    WorkflowRunId: context.WorkflowRunId.ToString("D"),
                    StepKey: step.StepKey, StepRevision: step.Revision,
                    PublishedAt: DateTimeOffset.UtcNow, WorkspaceRevisionId: workspaceRevision);
                await artifacts.WriteAsync(
                    new ArtifactWriteRequest<RepositoryReindexPublicationPayload>(
                        context.WorkflowRunId.ToString("D"), "repository-publication", 2, "post-generation", ArtifactStatus.Validated, publication),
                    token);
                return value;
            }, cancellationToken: cancellationToken);

            return input;
        }
    }
}
