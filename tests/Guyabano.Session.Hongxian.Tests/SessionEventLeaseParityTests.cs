using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Hongxian;
using Guyabano.Session.Sqlite;
using Penghou.Hongxian.Sqlite;

namespace Guyabano.Session.Hongxian.Tests;

/// <summary>
/// Event and lease parity: the same matrix runs against the duplicate
/// file-system/Siming kernel and the Hongxian-backed mapping layer.
/// </summary>
public sealed class SessionEventLeaseParityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-event-parity",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("filesystem")]
    [InlineData("hongxian")]
    public async Task Append_read_round_trip(string backend)
    {
        using var opened = Open(backend, nameof(Append_read_round_trip));
        var sessionId = GuyabanoSessionId.New();
        var causation = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        var appended = await opened.Events.AppendAsync(
            new SessionEventRequest(
                sessionId,
                "system",
                "user-message",
                DateTimeOffset.UtcNow,
                CausationId: causation,
                PayloadJson: """{"text":"hello"}""",
                PayloadSensitivity: SessionPayloadSensitivity.Confidential,
                PayloadRetention: SessionPayloadRetention.DigestOnly),
            ct);

        appended.Sequence.Should().BeGreaterThanOrEqualTo(0);
        appended.Actor.Should().Be("system");
        appended.EventType.Should().Be("user-message");
        appended.CausationId.Should().Be(causation);
        appended.PayloadSensitivity.Should().Be(SessionPayloadSensitivity.Confidential);
        appended.PayloadRetention.Should().Be(SessionPayloadRetention.DigestOnly);

        var read = await opened.Events.ReadAsync(sessionId, cancellationToken: ct);
        read.Should().ContainSingle().Which.EventId.Should().Be(appended.EventId);

        var page = await opened.Events.ReadPageAsync(
            new SessionEventPageRequest(sessionId, Limit: 10), ct);
        page.Events.Should().ContainSingle();
        page.HasMore.Should().BeFalse();

        var last = await opened.Events.VerifyChainAsync(sessionId, ct);
        last.Should().NotBeNull();
        last!.Sequence.Should().Be(appended.Sequence);
    }

    [Theory]
    [InlineData("filesystem")]
    [InlineData("hongxian")]
    public async Task Lease_acquire_reports_identity(string backend)
    {
        using var opened = Open(backend, nameof(Lease_acquire_reports_identity));
        // Hongxian fences leases on existing sessions; create first on both.
        var sessionId = (await opened.Sessions.CreateAsync(
            "repo:test", "workspace:test", cancellationToken: TestContext.Current.CancellationToken)).Id;
        var operationId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await using var lease = await opened.Leases.AcquireAsync(sessionId, operationId, ct);

        lease.SessionId.Should().Be(sessionId);
        lease.OperationId.Should().Be(operationId);
        lease.AcquiredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private OpenedStores Open(string backend, string name)
    {
        var directory = Path.Combine(root, backend, name);
        if (backend == "filesystem")
        {
            var sessions = new FileSystemGuyabanoSessionStore(Path.Combine(directory, "sessions"));
            var events = new Guyabano.Session.Sqlite.SimingSessionEventStore(Path.Combine(directory, "events"));
            var leases = new FileSystemSessionDecisionLeaseProvider(Path.Combine(directory, "leases"));
            return new OpenedStores(sessions, events, leases, [sessions, events]);
        }

        var set = new HongxianSqliteStoreSet(
            new HongxianSqliteOptions { RootPath = directory, Pooling = false });
        return new OpenedStores(
            new HongxianGuyabanoSessionStore(set.SessionStore),
            new HongxianGuyabanoSessionEventStore(set.Events),
            new HongxianDecisionLeaseProvider(set.DecisionLeases),
            [set]);
    }

    private sealed class OpenedStores(
        IGuyabanoSessionStore sessions,
        ISessionEventStore events,
        ISessionDecisionLeaseProvider leases,
        IReadOnlyList<IDisposable> lifetime) : IDisposable
    {
        public IGuyabanoSessionStore Sessions { get; } = sessions;

        public ISessionEventStore Events { get; } = events;

        public ISessionDecisionLeaseProvider Leases { get; } = leases;

        public void Dispose()
        {
            foreach (var disposable in lifetime)
            {
                disposable.Dispose();
            }
        }
    }
}
