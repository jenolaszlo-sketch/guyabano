using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.Prompting;
using Guyabano.Session;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Penghou.Cangjie.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

public sealed class GenerationArtifactPublicationTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-generation-manifest-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TaskContextAndGeneratedManifestPublishWithProvenanceAndSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync(
            "repo:test",
            "workspace:test",
            cancellationToken: cancellationToken);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, cancellationToken);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions
            {
                OutputRoot = rootPath,
                CiRelativePath = "."
            }),
            sessionStore);

        // Create session workspace with files so manifest can hash them
        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);
        var fileAPath = Path.Combine(workspace.HostPath, "src", "A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(fileAPath)!);
        await File.WriteAllTextAsync(fileAPath, "class A {}", cancellationToken);
        var fileBPath = Path.Combine(workspace.HostPath, "src", "B.cs");
        await File.WriteAllTextAsync(fileBPath, "class B {}", cancellationToken);

        var artifacts = new ZhinuPublishingArtifactRepository(
            new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts")),
            resolver);

        var workflow = new GenerationManifestWorkflow(artifacts, workspace);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("generation-proof", "1", workflow));

        await engine.StartAsync("generation-proof", "1", "write", runId, cancellationToken: cancellationToken);
        await engine.ExecuteAsync(runId, cancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: cancellationToken);

        var published = await engine.GetArtifactsAsync(runId, cancellationToken);
        published.Should().HaveCount(2);
        var taskContextArtifact = published.Single(a => a.ArtifactType == "task-context");
        var manifestArtifact = published.Single(a => a.ArtifactType == "generated-file-manifest");

        taskContextArtifact.Name.Should().Be("task-context/task-42");
        taskContextArtifact.ProducerStepKey.Should().Be("generate-task");
        taskContextArtifact.ContentHash.Should().NotBeNullOrWhiteSpace();
        taskContextArtifact.Metadata.Should().NotBeNull();
        taskContextArtifact.Metadata!["sessionId"].Should().Be(session.Id.ToString());
        taskContextArtifact.Metadata["artifactId"].Should().NotBeNullOrWhiteSpace();
        taskContextArtifact.ArtifactVersion.Should().Be("1");

        manifestArtifact.Name.Should().Be("generated-file-manifest/task-42");
        manifestArtifact.ProducerStepKey.Should().Be("generate-task");
        manifestArtifact.ContentHash.Should().NotBeNullOrWhiteSpace();
        manifestArtifact.Metadata!["sessionId"].Should().Be(session.Id.ToString());
        manifestArtifact.Metadata["artifactId"].Should().NotBeNullOrWhiteSpace();
        manifestArtifact.ArtifactVersion.Should().Be("1");
        manifestArtifact.Location.Should().EndWith(".json");

        // Verify the authoritative artifact can be read and has correct file hashes
        var envelope = await artifacts.ReadAsync<GeneratedFileManifest>(
            new ArtifactReference(
                manifestArtifact.Metadata["artifactId"],
                manifestArtifact.ArtifactType,
                int.Parse(manifestArtifact.ArtifactVersion),
                manifestArtifact.Location,
                manifestArtifact.ContentHash!),
            cancellationToken);
        envelope.Should().NotBeNull();
        envelope!.Payload.Files.Should().HaveCount(2);
        var expectedHashA = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("class A {}"))).ToLowerInvariant();
        var expectedHashB = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("class B {}"))).ToLowerInvariant();
        envelope.Payload.Files.Single(f => f.RelativePath == "src/A.cs").ContentHash.Should().Be(expectedHashA);
        envelope.Payload.Files.Single(f => f.RelativePath == "src/B.cs").ContentHash.Should().Be(expectedHashB);
        envelope.Payload.SessionId.Should().Be(session.Id.ToString());
        envelope.Payload.TaskId.Should().Be("task-42");
        envelope.Payload.StepKey.Should().Be("generate-task");
        envelope.Payload.WorkspaceRevisionId.Should().BeNull();
        envelope.WorkflowId.Should().Be(runId.ToString("D"));

        // Verify task-context wrapper is readable and contains typed references
        var taskEnvelope = await artifacts.ReadAsync<TaskContextArtifactPayload>(
            new ArtifactReference(
                taskContextArtifact.Metadata["artifactId"],
                taskContextArtifact.ArtifactType,
                int.Parse(taskContextArtifact.ArtifactVersion),
                taskContextArtifact.Location,
                taskContextArtifact.ContentHash!),
            cancellationToken);
        taskEnvelope.Should().NotBeNull();
        taskEnvelope!.Payload.SessionId.Should().Be(session.Id.ToString());
        taskEnvelope.Payload.WorkflowRunId.Should().Be(runId.ToString("D"));
        taskEnvelope.Payload.Context.TaskId.Should().Be("task-42");
        taskEnvelope.Payload.StepKey.Should().Be("generate-task");
    }

    [Fact]
    public async Task ManifestWriteIsIdempotentAcrossRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: cancellationToken);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, cancellationToken);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);
        var filePath = Path.Combine(workspace.HostPath, "src", "A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "class A {}", cancellationToken);

        var inner = new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts"));
        var artifacts = new ZhinuPublishingArtifactRepository(inner, resolver);
        var workflow = new IdempotentManifestWorkflow(artifacts, workspace);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("idempotent-proof", "1", workflow));

        await engine.StartAsync("idempotent-proof", "1", "write", runId, cancellationToken: cancellationToken);
        await engine.ExecuteAsync(runId, cancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: cancellationToken);

        var published = await engine.GetArtifactsAsync(runId, cancellationToken);
        // Two writes with same payload should not duplicate FS files but publish produces one logical artifact per kind/stageKey content hash
        published.Should().ContainSingle(a => a.ArtifactType == "generated-file-manifest");
        var manifestArtifact = published.Single(a => a.ArtifactType == "generated-file-manifest");

        // FS idempotency: second write with identical payload returns same reference without duplicating file
        var firstEnvelope = await inner.ReadLatestAsync<GeneratedFileManifest>(runId.ToString("D"), "generated-file-manifest", "task-42", cancellationToken);
        firstEnvelope.Should().NotBeNull();
        var secondEnvelope = await inner.WriteAsync(
            new ArtifactWriteRequest<GeneratedFileManifest>(
                runId.ToString("D"),
                "generated-file-manifest",
                1,
                "task-42",
                ArtifactStatus.Validated,
                firstEnvelope!.Payload),
            cancellationToken);
        secondEnvelope.Reference.ArtifactId.Should().Be(firstEnvelope.Reference.ArtifactId);
        secondEnvelope.Reference.ContentHash.Should().Be(firstEnvelope.Reference.ContentHash);
    }

    [Fact]
    public async Task RepositoryPublicationPublishesWithSessionAndProvenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: cancellationToken);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, cancellationToken);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var workspace = resolver.Resolve(session.Id);
        var artifacts = new ZhinuPublishingArtifactRepository(
            new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts")),
            resolver);
        var workflow = new RepositoryPublicationWorkflow(artifacts, workspace);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("repo-proof", "1", workflow));
        await engine.StartAsync("repo-proof", "1", "write", runId, cancellationToken: cancellationToken);
        await engine.ExecuteAsync(runId, cancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: cancellationToken);

        var published = await engine.GetArtifactsAsync(runId, cancellationToken);
        published.Should().ContainSingle(a => a.ArtifactType == "repository-publication");
        var artifact = published.Single(a => a.ArtifactType == "repository-publication");
        artifact.Name.Should().Be("repository-publication/repo:test");
        artifact.ProducerStepKey.Should().Be("index-repository");
        artifact.ContentHash.Should().NotBeNullOrWhiteSpace();
        artifact.Metadata!["sessionId"].Should().Be(session.Id.ToString());
        artifact.ArtifactVersion.Should().Be("1");

        var envelope = await artifacts.ReadAsync<RepositoryPublicationPayload>(
            new ArtifactReference(artifact.Metadata["artifactId"], artifact.ArtifactType, int.Parse(artifact.ArtifactVersion ?? "1"), artifact.Location, artifact.ContentHash!),
            cancellationToken);
        envelope.Should().NotBeNull();
        envelope!.Payload.Revision.RepositoryId.Should().Be("repo:test");
        envelope.Payload.SessionId.Should().Be(session.Id.ToString());
        envelope.WorkflowId.Should().Be(runId.ToString("D"));
    }

    [Fact]
    public async Task ValidationEvidencePublishesWithSessionAndProvenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: cancellationToken);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, cancellationToken);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);
        var artifacts = new ZhinuPublishingArtifactRepository(
            new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts")),
            resolver);
        var workflow = new ValidationEvidenceWorkflow(artifacts, workspace);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("validation-proof", "1", workflow));
        await engine.StartAsync("validation-proof", "1", "write", runId, cancellationToken: cancellationToken);
        await engine.ExecuteAsync(runId, cancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: cancellationToken);

        var published = await engine.GetArtifactsAsync(runId, cancellationToken);
        published.Should().ContainSingle(a => a.ArtifactType == "validation-evidence");
        var artifact = published.Single(a => a.ArtifactType == "validation-evidence");
        artifact.Name.Should().Be("validation-evidence/build-1");
        artifact.ProducerStepKey.Should().Be("build");
        artifact.ContentHash.Should().NotBeNullOrWhiteSpace();
        artifact.Metadata!["sessionId"].Should().Be(session.Id.ToString());

        var envelope = await artifacts.ReadAsync<ValidationEvidencePayload>(
            new ArtifactReference(artifact.Metadata["artifactId"], artifact.ArtifactType, int.Parse(artifact.ArtifactVersion ?? "1"), artifact.Location, artifact.ContentHash!),
            cancellationToken);
        envelope.Should().NotBeNull();
        envelope!.Payload.BuildResult.Succeeded.Should().BeTrue();
        envelope.Payload.SessionId.Should().Be(session.Id.ToString());
        envelope.Payload.WorkspaceRevisionId.Should().BeNull();
    }

    [Fact]
    public async Task Recovery_ReusesFilesystemAndCangjieAfterZhinuPublishMiss()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: cancellationToken);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, cancellationToken);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);

        var fileSystem = new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts"));
        var cangjieStore = new SqliteContextStore(new CangjieSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "cangjie-recovery.db"),
            Pooling = false
        });
        var indexingRepo = new ContextIndexingArtifactRepository(fileSystem, cangjieStore);
        var publishingRepo = new ZhinuPublishingArtifactRepository(indexingRepo, resolver);

        var workflowId = runId.ToString("D");
        var payload = new TestArtifact("recovery-payload");
        var request = new ArtifactWriteRequest<TestArtifact>(workflowId, "repository-publication", 1, "repo:test", ArtifactStatus.Validated, payload);

        // First write without Zhinu context: filesystem + Cangjie succeed, Zhinu is not published (simulates publish miss)
        var first = await publishingRepo.WriteAsync(request, cancellationToken);
        first.Reference.ContentHash.Should().NotBeNullOrWhiteSpace();

        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var workflow = new RecoveryWorkflow(publishingRepo, request);
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("recovery-proof", "1", workflow));
        await engine.StartAsync("recovery-proof", "1", "write", runId, cancellationToken: cancellationToken);
        await engine.ExecuteAsync(runId, cancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: cancellationToken);

        var published = await engine.GetArtifactsAsync(runId, cancellationToken);
        published.Should().ContainSingle(a => a.ArtifactType == "repository-publication");
        var artifact = published.Single(a => a.ArtifactType == "repository-publication");
        artifact.ContentHash.Should().Be(first.Reference.ContentHash);
        artifact.Metadata!["sessionId"].Should().Be(session.Id.ToString());
        artifact.Metadata["artifactId"].Should().Be(first.Reference.ArtifactId);

        // Cangjie should have single entry idempotent
        var contextId = CreateContextId(first.Reference.ArtifactId);
        var indexed = await cangjieStore.GetAsync(contextId, cancellationToken);
        indexed.Should().NotBeNull();
        indexed!.Metadata["contentHash"].Should().Be(first.Reference.ContentHash);

        // Second direct write without workflow should still be idempotent
        var second = await indexingRepo.WriteAsync(request, cancellationToken);
        second.Reference.ArtifactId.Should().Be(first.Reference.ArtifactId);
        second.Reference.ContentHash.Should().Be(first.Reference.ContentHash);
    }

    private static Guid CreateContextId(string artifactId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(artifactId));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed class RecoveryWorkflow(IArtifactRepository artifacts, ArtifactWriteRequest<TestArtifact> request) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            await context.StepAsync("recover", input, async (value, step, token) =>
            {
                using var scope = CodeGenerationZhinuStepScope.Push(step);
                await artifacts.WriteAsync(request, token);
                return value;
            }, cancellationToken: cancellationToken);
    }

    private sealed record TestArtifact(string Value);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private sealed class GenerationManifestWorkflow(
        IArtifactRepository artifacts,
        CodeGenerationWorkspace workspace) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            await context.StepAsync("generate-task", input, async (value, step, token) =>
            {
                using var scope = CodeGenerationZhinuStepScope.Push(step);
                var taskContext = new CodeGenerationTaskContext(
                    OriginalRequest: "request",
                    TaskId: "task-42",
                    TaskTitle: "Generate A",
                    Objective: "objective",
                    SolutionName: "Solution",
                    SolutionPath: "Solution.sln",
                    ProjectName: "Project",
                    ProjectPath: "Project/Project.csproj",
                    ProjectDirectory: "Project",
                    RootNamespace: "Project",
                    TargetFramework: "net10.0",
                    ModuleName: "Module",
                    ModuleResponsibilities: ["resp"],
                    Deliverables: ["deliv"],
                    Contracts: [],
                    AcceptanceCriteria: [],
                    Decisions: [],
                    Files: [],
                    SessionId: workspace.SessionId.ToString(),
                    WorkflowRunId: context.WorkflowRunId.ToString("D"),
                    WorkflowStepKey: step.StepKey);

                var wrapper = new TaskContextArtifactPayload(
                    Context: taskContext,
                    SessionId: workspace.SessionId.ToString(),
                    WorkflowRunId: context.WorkflowRunId.ToString("D"),
                    StepKey: step.StepKey,
                    StepRevision: step.Revision,
                    CangjieSnapshotId: Guid.NewGuid(),
                    CangjieStrategy: "hetu-public-surface-and-symbol-neighborhood",
                    CangjieStrategyVersion: "1",
                    HetuIndexRunId: "hetu-run-1",
                    HetuIndexIdentity: "workspace-revision-identity",
                    HetuProviderSnapshotIdentity: "provider-snapshot-1",
                    RetryContext: null);

                var taskContextEnvelope = await artifacts.WriteAsync(
                    new ArtifactWriteRequest<TaskContextArtifactPayload>(
                        context.WorkflowRunId.ToString("D"),
                        "task-context",
                        1,
                        "task-42",
                        ArtifactStatus.Validated,
                        wrapper),
                    token);

                var manifest = await GeneratedFileManifestFactory.CreateAsync(
                    sessionId: workspace.SessionId.ToString(),
                    workflowRunId: context.WorkflowRunId.ToString("D"),
                    stepKey: step.StepKey,
                    stepRevision: step.Revision,
                    workspaceHostPath: workspace.HostPath,
                    workspaceCiPath: workspace.CiRelativePath,
                    taskId: "task-42",
                    writtenFiles: [Path.Combine(workspace.HostPath, "src", "A.cs"), Path.Combine(workspace.HostPath, "src", "B.cs")],
                    skippedFiles: [],
                    parentTaskId: "parent-1",
                    model: "test-model",
                    modelTier: 1,
                    cancellationToken: token);

                await artifacts.WriteAsync(
                    new ArtifactWriteRequest<GeneratedFileManifest>(
                        context.WorkflowRunId.ToString("D"),
                        "generated-file-manifest",
                        1,
                        "task-42",
                        ArtifactStatus.Validated,
                        manifest,
                        [taskContextEnvelope.Reference]),
                    token);

                return value;
            }, cancellationToken: cancellationToken);
    }

    private sealed class IdempotentManifestWorkflow(
        IArtifactRepository artifacts,
        CodeGenerationWorkspace workspace) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            await context.StepAsync("generate-task", input, async (value, step, token) =>
            {
                using var scope = CodeGenerationZhinuStepScope.Push(step);
                var manifest = await GeneratedFileManifestFactory.CreateAsync(
                    sessionId: workspace.SessionId.ToString(),
                    workflowRunId: context.WorkflowRunId.ToString("D"),
                    stepKey: step.StepKey,
                    stepRevision: step.Revision,
                    workspaceHostPath: workspace.HostPath,
                    workspaceCiPath: workspace.CiRelativePath,
                    taskId: "task-42",
                    writtenFiles: [Path.Combine(workspace.HostPath, "src", "A.cs")],
                    skippedFiles: [],
                    cancellationToken: token);

                await artifacts.WriteAsync(
                    new ArtifactWriteRequest<GeneratedFileManifest>(
                        context.WorkflowRunId.ToString("D"),
                        "generated-file-manifest",
                        1,
                        "task-42",
                        ArtifactStatus.Validated,
                        manifest),
                    token);
                // Second write with identical payload (same content hash) should be idempotent
                await artifacts.WriteAsync(
                    new ArtifactWriteRequest<GeneratedFileManifest>(
                        context.WorkflowRunId.ToString("D"),
                        "generated-file-manifest",
                        1,
                        "task-42",
                        ArtifactStatus.Validated,
                        manifest),
                    token);
                return value;
            }, cancellationToken: cancellationToken);
    }

    private sealed class RepositoryPublicationWorkflow(IArtifactRepository artifacts, CodeGenerationWorkspace workspace) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            await context.StepAsync("index-repository", input, async (value, step, token) =>
            {
                using var scope = CodeGenerationZhinuStepScope.Push(step);
                var revision = new RepositoryRevision(
                    "repo:test", "/tmp/repo", "workspace-revision-abc", "run-1", "provider-snap-1", true, 10, 0, ["/tmp/repo/Sample.cs"]);
                var payload = new RepositoryPublicationPayload(revision, workspace.SessionId.ToString(), context.WorkflowRunId.ToString("D"), step.StepKey, step.Revision, DateTimeOffset.UtcNow);
                await artifacts.WriteAsync(
                    new ArtifactWriteRequest<RepositoryPublicationPayload>(context.WorkflowRunId.ToString("D"), "repository-publication", 1, "repo:test", ArtifactStatus.Validated, payload),
                    token);
                return value;
            }, cancellationToken: cancellationToken);
    }

    private sealed class ValidationEvidenceWorkflow(IArtifactRepository artifacts, CodeGenerationWorkspace workspace) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            await context.StepAsync("build", input, async (value, step, token) =>
            {
                using var scope = CodeGenerationZhinuStepScope.Push(step);
                var buildResult = new CodeGenerationBuildResult(true, 0, null, []);
                var payload = new ValidationEvidencePayload(buildResult, workspace.SessionId.ToString(), context.WorkflowRunId.ToString("D"), step.StepKey, step.Revision, workspace.HostPath, workspace.CiRelativePath, ["src/A.cs"], DateTimeOffset.UtcNow);
                await artifacts.WriteAsync(
                    new ArtifactWriteRequest<ValidationEvidencePayload>(context.WorkflowRunId.ToString("D"), "validation-evidence", 1, "build-1", ArtifactStatus.Validated, payload),
                    token);
                return value;
            }, cancellationToken: cancellationToken);
    }
}
