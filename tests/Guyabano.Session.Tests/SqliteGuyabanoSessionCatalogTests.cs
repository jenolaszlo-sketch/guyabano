using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;

namespace Guyabano.SessionTests;

public sealed class SqliteGuyabanoSessionCatalogTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-session-catalog-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Catalog_PersistsAndListsSessionsWithoutOpeningSessionStores()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteGuyabanoSessionCatalog(path, pooling: false);
        var olderId = GuyabanoSessionId.New();
        var newerId = GuyabanoSessionId.New();
        await first.CreateAsync("repo-a", $"workspace:{olderId}", olderId, ct);
        await first.CreateAsync("repo-b", $"workspace:{newerId}", newerId, ct);
        var runId = Guid.CreateVersion7();
        await first.AttachWorkflowRunAsync(newerId, runId, ct);
        await first.UpdateWorkspaceRevisionAsync(newerId, null, "revision-1", ct);

        var reopened = new SqliteGuyabanoSessionCatalog(path, pooling: false);
        var sessions = await reopened.ListAsync(ct);
        var session = await reopened.FindByWorkflowRunAsync(runId, ct);

        sessions.Should().HaveCount(2);
        session.Should().NotBeNull();
        session!.Id.Should().Be(newerId);
        session.WorkflowRunIds.Should().ContainSingle().Which.Should().Be(runId);
        session.CurrentWorkspaceRevision.Should().Be("revision-1");
        session.Version.Should().Be(2);
        (await reopened.ListPendingAsync(cancellationToken: ct))
            .Where(item => item.SessionId == newerId)
            .Select(item => item.EventType)
            .Should().Equal(
                SessionEventTypes.SessionCreated,
                SessionEventTypes.WorkflowAttached,
                SessionEventTypes.WorkspaceRevisionAccepted);
    }

    [Fact]
    public async Task AttachWorkflowRun_RejectsASecondSessionOwnerAcrossCatalogInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteGuyabanoSessionCatalog(path, pooling: false);
        var second = new SqliteGuyabanoSessionCatalog(path, pooling: false);
        var firstSession = await first.CreateAsync("repo", "workspace:one", cancellationToken: ct);
        var secondSession = await first.CreateAsync("repo", "workspace:two", cancellationToken: ct);
        var runId = Guid.CreateVersion7();
        await first.AttachWorkflowRunAsync(firstSession.Id, runId, ct);

        var action = () => second.AttachWorkflowRunAsync(secondSession.Id, runId, ct);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{firstSession.Id}*");
        (await second.FindByWorkflowRunAsync(runId, ct))!.Id.Should().Be(firstSession.Id);
    }

    [Fact]
    public async Task WorkspaceRevision_UsesCrossProcessCompareAndSwap()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteGuyabanoSessionCatalog(path, pooling: false);
        var second = new SqliteGuyabanoSessionCatalog(path, pooling: false);
        var session = await first.CreateAsync("repo", "workspace", cancellationToken: ct);

        var results = await Task.WhenAll(
            first.UpdateWorkspaceRevisionAsync(session.Id, null, "revision-a", ct),
            second.UpdateWorkspaceRevisionAsync(session.Id, null, "revision-b", ct));

        results.Count(item => item is not null).Should().Be(1);
        var stored = await first.GetAsync(session.Id, ct);
        stored!.CurrentWorkspaceRevision.Should().BeOneOf("revision-a", "revision-b");
    }

    [Fact]
    public async Task DecisionLease_SerializesIndependentCatalogInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteGuyabanoSessionCatalog(path, pooling: false);
        var second = new SqliteGuyabanoSessionCatalog(path, pooling: false);
        var session = await first.CreateAsync("repo", "workspace", cancellationToken: ct);
        var held = await first.AcquireAsync(session.Id, Guid.CreateVersion7(), ct);

        var blocked = second.AcquireAsync(session.Id, Guid.CreateVersion7(), ct).AsTask();
        await Task.Delay(100, ct);
        blocked.IsCompleted.Should().BeFalse();

        await held.DisposeAsync();
        await using var acquired = await blocked.WaitAsync(TimeSpan.FromSeconds(2), ct);
        acquired.SessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task LifecycleReceipts_AreDurableAndMarkedDeliveredIdempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new SqliteGuyabanoSessionCatalog(
            Path.Combine(rootPath, "catalog.db"),
            pooling: false);
        var session = await catalog.CreateAsync("repo", "workspace", cancellationToken: ct);
        await using (var lease = await catalog.AcquireAsync(
            session.Id,
            Guid.CreateVersion7(),
            ct))
        {
        }
        var pending = await catalog.ListPendingAsync(cancellationToken: ct);
        pending.Select(item => item.EventType).Should().Equal(
            SessionEventTypes.SessionCreated,
            SessionEventTypes.DecisionLeaseAcquired,
            SessionEventTypes.DecisionLeaseReleased);

        await catalog.MarkDeliveredAsync(pending[0].ReceiptId, DateTimeOffset.UtcNow, ct);
        await catalog.MarkDeliveredAsync(pending[0].ReceiptId, DateTimeOffset.UtcNow.AddMinutes(1), ct);

        (await catalog.ListPendingAsync(cancellationToken: ct))
            .Should().HaveCount(2);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
