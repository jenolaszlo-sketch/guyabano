using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.WorkflowWorker;
using Penghou.Cangjie.Sqlite;
using Penghou.Hetu;
using Penghou.Hetu.CSharp;
using Penghou.Zhinu;

namespace Guyabano.WorkflowProgressTests;

public sealed class RepositoryContextServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"guyabano-repository-context-{Guid.NewGuid():N}");

    [Fact]
    public async Task IndexSelectCapture_PinsExactContextForRestartReplay()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "CustomerService.cs"),
            "namespace Sample; public sealed class CustomerService { public void Run() { } }",
            TestContext.Current.CancellationToken);

        await using var hetu = new HetuHostBuilder()
            .AddCSharpPlugin()
            .Build();
        var databasePath = Path.Combine(root, "cangjie.db");
        var contextStore = CreateContextStore(databasePath);
        var service = new RepositoryContextService(hetu, contextStore);
        var workflowRunId = Guid.NewGuid();
        await new CangjieRevisionedConceptService(contextStore)
            .StoreKnowledgeAsync(
                "session:test",
                "customer-service-convention",
                "Customer service decisions require cancellation tokens.",
                workflowRunId.ToString("D"),
                "clarification/accepted",
                1,
                cancellationToken: TestContext.Current.CancellationToken);
        var revision = await ExecuteAsync(
            new IndexRepositoryStep(
                service,
                new FileSystemArtifactRepository(Path.Combine(root, ".gen", "artifacts")),
                new CodeGenerationActivityHeartbeatStore(
                    TimeProvider.System)),
            new RepositoryIndexRequest(
                new RepositoryReference("repo:test", root),
                workflowRunId.ToString("D"),
                "session:test"),
            workflowRunId,
            "repository/index");

        var selection = await ExecuteAsync(
            new SelectRepositoryContextStep(
                service,
                new CodeGenerationActivityHeartbeatStore(
                    TimeProvider.System)),
            new RepositoryContextSelectionRequest(revision, []),
            workflowRunId,
            "repository/select");
        var captured = await ExecuteAsync(
            new CaptureRepositoryContextStep(
                service,
                new CodeGenerationActivityHeartbeatStore(
                    TimeProvider.System)),
            new RepositoryContextCaptureRequest(
                selection,
                workflowRunId.ToString("D"),
                "session:test",
                "customer service"),
            workflowRunId,
            "repository/capture");

        revision.WorkspaceRevision.Should().HaveLength(64);
        selection.Observations.Should().Contain(item =>
            item.Content.Contains("CustomerService", StringComparison.Ordinal));
        captured.Content.Should().Contain("CustomerService");
        captured.Content.Should().Contain(
            "Customer service decisions require cancellation tokens.");
        captured.ItemCount.Should().BePositive();

        var reopened = CreateContextStore(databasePath);
        var replay = await reopened.ResolveSnapshotAsync(
            captured.SnapshotId,
            TestContext.Current.CancellationToken);
        replay!.Snapshot.Metadata["sessionId"].Should().Be("session:test");
        replay.Items.Should().OnlyContain(item =>
            item.Provenance.Attributes["sessionId"] == "session:test" &&
            item.Tags.Contains("session:session:test"));

        replay.Should().NotBeNull();
        replay!.Items.Select(item => item.Content)
            .Should().Contain(item =>
                item.Contains("CustomerService", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Select_RejectsARevisionAfterHetuPublishesNewerSourceState()
    {
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "Sample.csproj");
        var sourcePath = Path.Combine(root, "CustomerService.cs");
        await File.WriteAllTextAsync(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            sourcePath,
            "namespace Sample; public sealed class CustomerService { }",
            TestContext.Current.CancellationToken);
        await using var hetu = new HetuHostBuilder()
            .AddCSharpPlugin()
            .Build();
        var service = new RepositoryContextService(
            hetu,
            CreateContextStore(Path.Combine(root, "publication-change.db")));
        var firstRunId = Guid.NewGuid();
        var original = await ExecuteAsync(
            new IndexRepositoryStep(
                service,
                new FileSystemArtifactRepository(Path.Combine(root, ".gen", "artifacts")),
                new CodeGenerationActivityHeartbeatStore(TimeProvider.System)),
            new RepositoryIndexRequest(
                new RepositoryReference("repo:changing", root),
                firstRunId.ToString("D"),
                "session:test"),
            firstRunId,
            "repository/index");

        await File.WriteAllTextAsync(
            sourcePath,
            "namespace Sample; public sealed class CustomerService { public void Run() { } }",
            TestContext.Current.CancellationToken);
        var secondRunId = Guid.NewGuid();
        var current = await ExecuteAsync(
            new IndexRepositoryStep(
                service,
                new FileSystemArtifactRepository(Path.Combine(root, ".gen", "artifacts")),
                new CodeGenerationActivityHeartbeatStore(TimeProvider.System)),
            new RepositoryIndexRequest(
                new RepositoryReference("repo:changing", root),
                secondRunId.ToString("D"),
                "session:test"),
            secondRunId,
            "repository/index");
        var selectOriginal = () => service.SelectAsync(
            new RepositoryContextSelectionRequest(original, []),
            TestContext.Current.CancellationToken);

        current.WorkspaceRevision.Should().NotBe(original.WorkspaceRevision);
        await selectOriginal.Should()
            .ThrowAsync<CodeGraphPublicationChangedException>();
    }

    [Fact]
    public async Task CaptureRetry_ReusesDeterministicSnapshot()
    {
        Directory.CreateDirectory(root);
        await using var hetu = new HetuHostBuilder().Build();
        var service = new RepositoryContextService(
            hetu,
            CreateContextStore(Path.Combine(root, "retry.db")));
        var workflowRunId = Guid.NewGuid().ToString("D");
        var revision = new RepositoryRevision(
            "repo:retry",
            root,
            new string('a', 64),
            "run:retry",
            null,
            false,
            0,
            0,
            []);
        var selection = new RepositoryContextSelection(
            revision,
            RepositoryContextService.SelectionStrategy,
            RepositoryContextService.SelectionStrategyVersion,
            [
                new RepositoryContextObservation(
                    "summary",
                    "Exact retry content.",
                    "hetu://repo-retry/publication/run-retry/summary")
            ]);
        var request = new RepositoryContextCaptureRequest(
            selection,
            workflowRunId,
            "session:test",
            "retry");

        var first = await service.CaptureAsync(
            request,
            TestContext.Current.CancellationToken);
        var second = await service.CaptureAsync(
            request,
            TestContext.Current.CancellationToken);

        second.SnapshotId.Should().Be(first.SnapshotId);
        second.Content.Should().Be(first.Content);
        second.ItemCount.Should().Be(first.ItemCount);
    }

    [Fact]
    public void PlanningRequest_DoesNotDiscloseRepositoryContextByDefault()
    {
        var request = RequestWithContext("private source-derived context");

        var prompt = CodeGenerationPlanningActivities.BuildPlanningRequest(
            request,
            new CodeGenerationWorkerOptions());

        prompt.Should().Be(request.Prompt);
        prompt.Should().NotContain("private source-derived context");
    }

    [Fact]
    public void PlanningRequest_UsesExplicitDisclosureBound()
    {
        var request = RequestWithContext("1234567890");

        var prompt = CodeGenerationPlanningActivities.BuildPlanningRequest(
            request,
            new CodeGenerationWorkerOptions
            {
                IncludeRepositoryContextInPrompts = true,
                RepositoryContextMaximumPromptCharacters = 5
            });

        prompt.Should().Contain("12345");
        prompt.Should().NotContain("123456");
        prompt.Should().Contain("truncated");
        prompt.Should().Contain(request.RepositoryContext!.SnapshotId.ToString("D"));
    }

    [Fact]
    public void SessionContextAssembler_BindsSnapshotAndExactHetuRevision()
    {
        var request = RequestWithContext("accepted decision and symbol edge");

        var assembled = SessionContextAssembler.Assemble(
            request.RepositoryContext,
            "code-generation",
            1000);

        assembled.Should().NotBeNull();
        assembled!.Content.Should().Contain("accepted decision and symbol edge");
        assembled.Content.Should().Contain(
            request.RepositoryContext!.SnapshotId.ToString("D"));
        assembled.Content.Should().Contain("run:test");
        assembled.Content.Should().Contain(new string('a', 64));
        assembled.Content.Should().Contain("untrusted reference data");
        assembled.Truncated.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static SqliteContextStore CreateContextStore(string path) =>
        new(new CangjieSqliteOptions
        {
            DatabasePath = path,
            Pooling = false
        });

    private static CodeGenerationWorkflowRequest RequestWithContext(
        string content) =>
        new(
            "Build the requested system.",
            Guyabano.Session.GuyabanoSessionId.New())
        {
            RepositoryContext = new RepositoryContextReference(
                Guid.NewGuid(),
                new RepositoryRevision(
                    "repo:test",
                    ".",
                    new string('a', 64),
                    "run:test",
                    null,
                    false,
                    1,
                    1,
                    ["Sample.csproj"]),
                "test-strategy",
                "1",
                content,
                1)
        };

    private static Task<TOutput> ExecuteAsync<TInput, TOutput>(
        WorkflowStep<TInput, TOutput> step,
        TInput input,
        Guid workflowRunId,
        string stepKey) =>
        step.ExecuteAsync(
            new WorkflowStepContext(
                workflowRunId,
                Guid.NewGuid(),
                stepKey,
                attempt: 1,
                revision: 0,
                isCompensation: false),
            input,
            TestContext.Current.CancellationToken);
}
