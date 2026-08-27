using FluentAssertions;
using Guyabano.Session;

namespace Guyabano.SessionTests;

public sealed class FileSystemGuyabanoSessionStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-session-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void NewSessionIdUsesUuidVersionSeven()
    {
        var sessionId = GuyabanoSessionId.New();

        sessionId.Value.Version.Should().Be(7);
    }

    [Fact]
    public async Task MultipleWorkflowRunsRemainAssociatedWithOneSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var store = new FileSystemGuyabanoSessionStore(rootPath);
        var session = await store.CreateAsync(
            "repo:generated",
            "workspace:generated",
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

        var reloaded = await store.GetAsync(session.Id, cancellationToken);
        reloaded.Should().NotBeNull();
        reloaded!.WorkflowRunIds.Should().Equal(firstRun, secondRun);
        (await store.FindByWorkflowRunAsync(firstRun, cancellationToken))!.Id
            .Should().Be(session.Id);
        (await store.FindByWorkflowRunAsync(secondRun, cancellationToken))!.Id
            .Should().Be(session.Id);
    }

    [Fact]
    public async Task AttachingSameWorkflowRunIsIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var store = new FileSystemGuyabanoSessionStore(rootPath);
        var session = await store.CreateAsync(
            "repo:generated",
            "workspace:generated",
            cancellationToken: cancellationToken);
        var runId = Guid.NewGuid();

        await store.AttachWorkflowRunAsync(
            session.Id,
            runId,
            cancellationToken);
        await store.AttachWorkflowRunAsync(
            session.Id,
            runId,
            cancellationToken);

        var reloaded = await store.GetAsync(session.Id, cancellationToken);
        reloaded!.WorkflowRunIds.Should().Equal(runId);
    }

    [Fact]
    public async Task SessionSurvivesStoreRestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        GuyabanoSession created;
        var runId = Guid.NewGuid();
        using (var first = new FileSystemGuyabanoSessionStore(rootPath))
        {
            created = await first.CreateAsync(
                "repo:generated",
                "workspace:generated",
                cancellationToken: cancellationToken);
            await first.AttachWorkflowRunAsync(
                created.Id,
                runId,
                cancellationToken);
        }

        using var restarted = new FileSystemGuyabanoSessionStore(rootPath);
        var reloaded = await restarted.FindByWorkflowRunAsync(
            runId,
            cancellationToken);

        reloaded.Should().BeEquivalentTo(created with
        {
            WorkflowRunIds = [runId]
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
