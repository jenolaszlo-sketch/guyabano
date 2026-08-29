using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Microsoft.Data.Sqlite;

namespace Guyabano.SessionTests;

public sealed class SqliteSessionProjectionStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(), "guyabano-session-projection-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Append_UpdatesRebuildableCurrentStateProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"), projectionStore: projections);
        var sessionId = GuyabanoSessionId.New();
        var workflowRun = Guid.CreateVersion7();
        var requested = await events.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.InputRequested,
            DateTimeOffset.UtcNow, CorrelationId: workflowRun), ct);
        await events.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.InputProvided,
            DateTimeOffset.UtcNow, CausationId: requested.EventId,
            CorrelationId: workflowRun), ct);
        await events.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.WorkspacePromoted,
            DateTimeOffset.UtcNow, CrossSystemRefs: new Dictionary<string, string>
            {
                ["toRevision"] = "workspace:2"
            }), ct);

        var snapshot = await projections.GetAsync(sessionId, ct);

        snapshot.Should().NotBeNull();
        snapshot!.AppliedSequence.Should().Be(3);
        snapshot.HeadHash.Should().Be((await events.VerifyChainAsync(sessionId, ct))!.Hash);
        snapshot.State.TotalEvents.Should().Be(3);
        snapshot.State.PendingInputEventIds.Should().BeEmpty();
        snapshot.State.CurrentWorkspaceRevision.Should().Be("workspace:2");
        snapshot.State.LastCommittedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Rebuild_RepairsMissingOrDeletedProjectionFromLedger()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(Path.Combine(rootPath, "sessions"));
        var sessionId = GuyabanoSessionId.New();
        await events.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
        await events.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.WorkflowStarted,
            DateTimeOffset.UtcNow, CorrelationId: Guid.CreateVersion7()), ct);
        var history = await events.ReadAsync(sessionId, cancellationToken: ct);

        var rebuilt = await projections.RebuildAsync(sessionId, history, ct);

        rebuilt!.AppliedSequence.Should().Be(2);
        rebuilt.HeadHash.Should().Be(history[^1].Hash);
        rebuilt.State.TotalEvents.Should().Be(2);
        (await projections.ListAsync(ct)).Should().ContainSingle(item => item.SessionId == sessionId);
    }

    [Fact]
    public async Task Apply_SameSequenceFromDifferentChain_RejectsHeadConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(Path.Combine(rootPath, "sessions"));
        var sessionId = GuyabanoSessionId.New();
        var committed = await events.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
        await projections.ApplyAsync(committed, ct);

        var conflict = () => projections.ApplyAsync(committed with { Hash = new string('0', 64) }, ct);

        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*head conflict*");
    }

    [Fact]
    public async Task DeliveryCursor_ExposesLagAndClearsItAtomicallyWithProjectionApply()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"));
        var sessionId = GuyabanoSessionId.New();
        var committed = await events.AppendAsync(new SessionEventRequest(
            sessionId,
            "user",
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow), ct);

        await projections.RecordCommittedAsync(committed, ct);
        var lagging = await projections.GetDeliveryStatusAsync(sessionId, ct);

        lagging!.IsLagging.Should().BeTrue();
        lagging.CommittedSequence.Should().Be(1);
        lagging.AppliedSequence.Should().Be(0);
        (await projections.ListLaggingAsync(cancellationToken: ct))
            .Should().ContainSingle();

        await projections.ApplyAsync(committed, ct);
        var current = await projections.GetDeliveryStatusAsync(sessionId, ct);
        current!.IsLagging.Should().BeFalse();
        current.AppliedSequence.Should().Be(1);
        current.AppliedHeadHash.Should().Be(committed.Hash);
        current.LastFailureType.Should().BeNull();
    }

    [Fact]
    public async Task DeliveryCursor_PersistsBoundedFailureDiagnostics()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"));
        var committed = await events.AppendAsync(new SessionEventRequest(
            GuyabanoSessionId.New(),
            "user",
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow), ct);
        await projections.RecordCommittedAsync(committed, ct);

        await projections.RecordFailureAsync(
            committed,
            new InvalidOperationException(new string('x', 3000)),
            ct);

        var status = await projections.GetDeliveryStatusAsync(committed.SessionId, ct);
        status!.IsLagging.Should().BeTrue();
        status.LastFailureType.Should().Be(typeof(InvalidOperationException).FullName);
        status.LastFailureDetail.Should().HaveLength(2048);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }
}
