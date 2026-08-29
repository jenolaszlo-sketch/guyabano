using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guyabano.WorkflowProgressTests;

public sealed class SessionProjectionRepairTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-projection-repair-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Repair_RediscoversLedgerEventsWithoutARecordedLagMarker()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sessions = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, "catalog"));
        var session = await sessions.CreateAsync(
            "repo", "workspace", cancellationToken: ct);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"));
        await events.AppendAsync(new SessionEventRequest(
            session.Id,
            "user",
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow), ct);
        await events.AppendAsync(new SessionEventRequest(
            session.Id,
            "guyabano",
            SessionEventTypes.WorkflowStarted,
            DateTimeOffset.UtcNow,
            CorrelationId: Guid.CreateVersion7()), ct);
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "projection.db"), pooling: false);
        var repair = new SessionProjectionRepairService(
            sessions,
            events,
            projections,
            projections,
            TimeProvider.System,
            NullLogger<SessionProjectionRepairService>.Instance);

        (await repair.RepairPendingAsync(maximumEventsPerSession: 1, ct))
            .Should().Be(1);
        (await projections.GetAsync(session.Id, ct))!.AppliedSequence.Should().Be(1);
        (await repair.RepairPendingAsync(maximumEventsPerSession: 1, ct))
            .Should().Be(1);

        var snapshot = await projections.GetAsync(session.Id, ct);
        var status = await projections.GetDeliveryStatusAsync(session.Id, ct);
        snapshot!.AppliedSequence.Should().Be(2);
        status!.IsLagging.Should().BeFalse();
        status.CommittedSequence.Should().Be(2);
        (await repair.RepairPendingAsync(cancellationToken: ct)).Should().Be(0);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
