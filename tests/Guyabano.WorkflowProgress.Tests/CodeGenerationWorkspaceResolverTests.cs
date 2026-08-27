using FluentAssertions;
using Guyabano.Session;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Options;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationWorkspaceResolverTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-workspace-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WorkflowRunsInSameSessionResolveSameWorkspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var store = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions"));
        var session = await store.CreateAsync(
            "repo:test",
            "workspace:test",
            cancellationToken: cancellationToken);
        var firstRun = Guid.NewGuid();
        var secondRun = Guid.NewGuid();
        await store.AttachWorkflowRunAsync(
            session.Id,
            firstRun,
            cancellationToken);
        await store.AttachWorkflowRunAsync(
            session.Id,
            secondRun,
            cancellationToken);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions
            {
                OutputRoot = rootPath,
                CiRelativePath = "."
            }),
            store);

        var first = await resolver.ResolveWorkflowAsync(
            firstRun.ToString("D"),
            cancellationToken);
        var second = await resolver.ResolveWorkflowAsync(
            secondRun.ToString("D"),
            cancellationToken);

        first.Should().Be(second);
        first.HostPath.Should().Be(Path.Combine(
            rootPath,
            "sessions",
            session.Id.ToString(),
            "workspace"));
        first.CiRelativePath.Should().Be(
            $"sessions/{session.Id}/workspace");
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
