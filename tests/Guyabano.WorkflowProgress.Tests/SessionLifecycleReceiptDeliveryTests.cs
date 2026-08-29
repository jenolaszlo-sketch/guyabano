using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guyabano.WorkflowProgressTests;

public sealed class SessionLifecycleReceiptDeliveryTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-lifecycle-delivery-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Delivery_AppendsCatalogReceiptsToSimingExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new SqliteGuyabanoSessionCatalog(
            Path.Combine(rootPath, "catalog.db"),
            pooling: false);
        var session = await catalog.CreateAsync(
            "repo", "workspace", cancellationToken: ct);
        var runId = Guid.CreateVersion7();
        await catalog.AttachWorkflowRunAsync(session.Id, runId, ct);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"));
        var delivery = new SessionLifecycleReceiptDeliveryService(
            catalog,
            events,
            TimeProvider.System,
            NullLogger<SessionLifecycleReceiptDeliveryService>.Instance);

        (await delivery.DeliverPendingAsync(ct)).Should().Be(2);
        (await delivery.DeliverPendingAsync(ct)).Should().Be(0);

        var history = await events.ReadAsync(session.Id, cancellationToken: ct);
        history.Select(item => item.EventType).Should().Equal(
            SessionEventTypes.SessionCreated,
            SessionEventTypes.WorkflowAttached);
        history.Select(item => item.IdempotencyKey).Should().OnlyHaveUniqueItems();
        (await catalog.ListPendingAsync(cancellationToken: ct)).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
