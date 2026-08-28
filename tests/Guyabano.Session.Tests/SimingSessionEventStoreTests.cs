using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Microsoft.Data.Sqlite;

namespace Guyabano.SessionTests;

public sealed class SimingSessionEventStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "guyabano-siming-session-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Append_ReopenAndVerify_PreservesDomainEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = GuyabanoSessionId.New();
        var correlationId = Guid.CreateVersion7();
        SessionEvent first;
        await using (var writer = new SimingSessionEventStore(rootPath))
        {
            first = await writer.AppendAsync(new SessionEventRequest(
                sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow,
                CorrelationId: correlationId, PayloadJson: "{\"prompt\":\"hello\"}"), ct);
        }

        await using var reader = new SimingSessionEventStore(rootPath);
        var events = await reader.ReadAsync(sessionId, cancellationToken: ct);
        events.Should().ContainSingle().Which.Should().BeEquivalentTo(first);
        first.SchemaVersion.Should().Be(1);
        first.CommittedAt.Should().NotBe(default);
        File.Exists(reader.GetLedgerPath(sessionId)).Should().BeTrue();
        (await reader.VerifyChainAsync(sessionId, ct)).Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task Append_UsesOneIndependentContiguousChainPerSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var firstSession = GuyabanoSessionId.New();
        var secondSession = GuyabanoSessionId.New();
        var first = await store.AppendAsync(new SessionEventRequest(
            firstSession, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
        var second = await store.AppendAsync(new SessionEventRequest(
            secondSession, "guyabano", SessionEventTypes.WorkflowStarted, DateTimeOffset.UtcNow), ct);

        first.Sequence.Should().Be(1);
        second.Sequence.Should().Be(1);
        first.PreviousHash.Should().BeNull();
        second.PreviousHash.Should().BeNull();
        store.GetLedgerPath(firstSession).Should().NotBe(store.GetLedgerPath(secondSession));
    }

    [Fact]
    public async Task Append_IdempotencyIsScopedPerSessionAndRetrySafe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var firstSession = GuyabanoSessionId.New();
        var secondSession = GuyabanoSessionId.New();
        var request = new SessionEventRequest(firstSession, "guyabano", SessionEventTypes.OperationPrepared,
            DateTimeOffset.UtcNow, IdempotencyKey: "operation:prepared");

        var first = await store.AppendAsync(request, ct);
        var replay = await store.AppendAsync(request with { OccurredAt = request.OccurredAt.AddMinutes(1) }, ct);
        var otherSession = await store.AppendAsync(request with { SessionId = secondSession }, ct);

        replay.Should().BeEquivalentTo(first);
        otherSession.EventId.Should().NotBe(first.EventId);
        otherSession.Sequence.Should().Be(1);
        (await store.ReadAsync(firstSession, cancellationToken: ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task ReadPage_ReturnsBoundedStableCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        for (var index = 0; index < 5; index++)
            await store.AppendAsync(new SessionEventRequest(
                sessionId, "guyabano", $"event-{index}", DateTimeOffset.UtcNow), ct);

        var first = await store.ReadPageAsync(new SessionEventPageRequest(sessionId, Limit: 2), ct);
        var second = await store.ReadPageAsync(new SessionEventPageRequest(sessionId, first.NextSequence!.Value, 2), ct);
        var third = await store.ReadPageAsync(new SessionEventPageRequest(sessionId, second.NextSequence!.Value, 2), ct);

        first.Events.Select(item => item.Sequence).Should().Equal(1, 2);
        second.Events.Select(item => item.Sequence).Should().Equal(3, 4);
        third.Events.Select(item => item.Sequence).Should().Equal(5);
        first.HasMore.Should().BeTrue();
        second.HasMore.Should().BeTrue();
        third.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Append_WhenProjectionFails_RetryReplaysCommittedEventAndHealsProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var projection = new FailOnceProjectionStore();
        await using var store = new SimingSessionEventStore(rootPath, projectionStore: projection);
        var sessionId = GuyabanoSessionId.New();
        var request = new SessionEventRequest(
            sessionId,
            "guyabano",
            SessionEventTypes.WorkflowStarted,
            DateTimeOffset.UtcNow,
            IdempotencyKey: "workflow:start");

        var firstAttempt = () => store.AppendAsync(request, ct);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("projection unavailable");

        var replay = await store.AppendAsync(request, ct);

        replay.Sequence.Should().Be(1);
        (await store.ReadAsync(sessionId, cancellationToken: ct)).Should().ContainSingle();
        projection.Applied.Should().ContainSingle().Which.Should().BeEquivalentTo(replay);
        projection.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Append_DigestOnlyPayload_RedactsContentAndKeepsRetryIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        var request = new SessionEventRequest(
            sessionId,
            "user",
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow,
            PayloadJson: "a sensitive clarification",
            IdempotencyKey: "message:1",
            PayloadSensitivity: SessionPayloadSensitivity.Confidential,
            PayloadRetention: SessionPayloadRetention.DigestOnly);

        var committed = await store.AppendAsync(request, ct);
        var replay = await store.AppendAsync(request with { OccurredAt = request.OccurredAt.AddMinutes(1) }, ct);

        committed.PayloadJson.Should().BeNull();
        committed.PayloadDigest.Should().StartWith("sha256:utf8:v1:");
        committed.PayloadSensitivity.Should().Be(SessionPayloadSensitivity.Confidential);
        committed.PayloadRetention.Should().Be(SessionPayloadRetention.DigestOnly);
        replay.Should().BeEquivalentTo(committed);

        var conflict = () => store.AppendAsync(request with { PayloadJson = "different" }, ct);
        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already used by a different event*");
    }

    [Fact]
    public async Task Append_OmittedPayload_PersistsNeitherContentNorDigest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var committed = await store.AppendAsync(new SessionEventRequest(
            GuyabanoSessionId.New(),
            "user",
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow,
            PayloadJson: "do not persist",
            PayloadSensitivity: SessionPayloadSensitivity.Restricted,
            PayloadRetention: SessionPayloadRetention.Omit), ct);

        committed.PayloadJson.Should().BeNull();
        committed.PayloadDigest.Should().BeNull();
        committed.PayloadRetention.Should().Be(SessionPayloadRetention.Omit);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }

    private sealed class FailOnceProjectionStore : ISessionProjectionStore
    {
        public int Attempts { get; private set; }

        public List<SessionEvent> Applied { get; } = [];

        public Task ApplyAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1) throw new InvalidOperationException("projection unavailable");
            Applied.Add(sessionEvent);
            return Task.CompletedTask;
        }

        public Task<SessionProjectionSnapshot?> GetAsync(
            GuyabanoSessionId sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionProjectionSnapshot?>(null);

        public Task<IReadOnlyList<SessionProjectionSnapshot>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionProjectionSnapshot>>([]);

        public Task<SessionProjectionSnapshot?> RebuildAsync(
            GuyabanoSessionId sessionId,
            IReadOnlyList<SessionEvent> events,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionProjectionSnapshot?>(null);
    }
}
