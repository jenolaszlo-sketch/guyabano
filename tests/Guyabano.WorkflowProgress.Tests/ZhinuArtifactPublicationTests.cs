using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.Session;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

public sealed class ZhinuArtifactPublicationTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-zhinu-artifact-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ArtifactWritePublishesProducingStepReferenceToZhinu()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync(
            "repo:test",
            "workspace:test",
            cancellationToken: cancellationToken);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(
            session.Id,
            runId,
            cancellationToken);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions
            {
                OutputRoot = rootPath,
                CiRelativePath = "."
            }),
            sessionStore);
        var artifacts = new ZhinuPublishingArtifactRepository(
            new FileSystemArtifactRepository(
                Path.Combine(rootPath, ".gen", "artifacts")),
            resolver);
        var workflow = new ArtifactPublishingWorkflow(artifacts);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("artifact-proof", "1", workflow));

        await engine.StartAsync(
            "artifact-proof",
            "1",
            "write",
            runId,
            cancellationToken: cancellationToken);
        await engine.ExecuteAsync(runId, cancellationToken);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: cancellationToken);

        var published = await engine.GetArtifactsAsync(
            runId,
            cancellationToken);
        published.Should().ContainSingle();
        published[0].Name.Should().Be("architecture/architecture-v1");
        published[0].ProducerStepKey.Should().Be("publish-artifact");
        published[0].Metadata.Should().NotBeNull();
        published[0].Metadata!["sessionId"].Should().Be(
            session.Id.ToString());
        published[0].ContentHash.Should().NotBeNullOrWhiteSpace();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private sealed class ArtifactPublishingWorkflow(
        IArtifactRepository artifacts) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            await context.StepAsync(
                "publish-artifact",
                input,
                async (value, step, token) =>
                {
                    using var scope = CodeGenerationZhinuStepScope.Push(step);
                    await artifacts.WriteAsync(
                        new ArtifactWriteRequest<TestArtifact>(
                            context.WorkflowRunId.ToString("D"),
                            "architecture",
                            1,
                            "architecture-v1",
                            ArtifactStatus.Validated,
                            new TestArtifact(value)),
                        token);
                    return value;
                },
                cancellationToken: cancellationToken);
    }

    private sealed record TestArtifact(string Value);
}
