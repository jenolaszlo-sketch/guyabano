using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Guyabano.WorkflowWorker;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

public sealed class SessionWorkflowInputServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-input-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProvideAsync_IdenticalRetryBuffersOneSignalAndClearsPendingInput()
    {
        var fixture = await CreateFixtureAsync();
        using var events = fixture.Events;
        var responseId = Guid.NewGuid();

        var applied = await fixture.Service.ProvideAsync(
            fixture.RunId,
            fixture.Request.EventId,
            responseId,
            "clarification",
            "user-42",
            "use SQLite",
            TestContext.Current.CancellationToken);
        var replayed = await fixture.Service.ProvideAsync(
            fixture.RunId,
            fixture.Request.EventId,
            responseId,
            "clarification",
            "user-42",
            "use SQLite",
            TestContext.Current.CancellationToken);

        applied.WasBuffered.Should().BeTrue();
        replayed.WasBuffered.Should().BeFalse();
        replayed.SessionEventId.Should().Be(applied.SessionEventId);
        replayed.ZhinuEventSequence.Should().Be(applied.ZhinuEventSequence);
        await fixture.Engine.ExecuteAsync(
            fixture.RunId,
            TestContext.Current.CancellationToken);
        (await fixture.Engine.WaitForCompletionAsync<string>(
            fixture.RunId,
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().Be("use SQLite");
        var history = await events.ReadAsync(
            fixture.Session.Id,
            cancellationToken: TestContext.Current.CancellationToken);
        history.Count(item => item.EventType == SessionEventTypes.InputProvided)
            .Should().Be(1);
        SessionTimelineProjection.Project(history).PendingInputEventIds.Should().BeEmpty();
        (await fixture.Engine.GetEventsAsync(
            fixture.RunId,
            cancellationToken: TestContext.Current.CancellationToken))
            .Count(item => item.EventType == WorkflowEventTypes.SignalSent)
            .Should().Be(1);
    }

    [Fact]
    public async Task ProvideAsync_AfterZhinuCommitGap_ReusesReceiptAndRepairsSessionEvidence()
    {
        var fixture = await CreateFixtureAsync();
        using var events = fixture.Events;
        await fixture.Engine.SendSignalWithReceiptAsync(
            fixture.RunId,
            "clarification",
            new SignalSendOptions { SignalId = fixture.Request.EventId },
            "use SQLite",
            TestContext.Current.CancellationToken);

        var repaired = await fixture.Service.ProvideAsync(
            fixture.RunId,
            fixture.Request.EventId,
            Guid.NewGuid(),
            "clarification",
            "user-42",
            "use SQLite",
            TestContext.Current.CancellationToken);

        repaired.WasBuffered.Should().BeFalse();
        var history = await events.ReadAsync(
            fixture.Session.Id,
            cancellationToken: TestContext.Current.CancellationToken);
        history.Should().ContainSingle(item =>
            item.EventType == SessionEventTypes.InputProvided &&
            item.CausationId == fixture.Request.EventId);
        (await fixture.Engine.GetSignalsAsync(
            fixture.RunId,
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task ProvideAsync_ConflictingResponseCannotBufferAnotherSignal()
    {
        var fixture = await CreateFixtureAsync();
        using var events = fixture.Events;
        await fixture.Service.ProvideAsync(
            fixture.RunId,
            fixture.Request.EventId,
            Guid.NewGuid(),
            "clarification",
            "user-42",
            "use SQLite",
            TestContext.Current.CancellationToken);

        var action = () => fixture.Service.ProvideAsync(
            fixture.RunId,
            fixture.Request.EventId,
            Guid.NewGuid(),
            "clarification",
            "user-43",
            "use PostgreSQL",
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<WorkflowOperationConflictException>();
        (await fixture.Engine.GetSignalsAsync(
            fixture.RunId,
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().ContainSingle();
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(root, "sessions"));
        var session = await sessionStore.CreateAsync(
            "repo:test",
            "workspace:test",
            cancellationToken: cancellationToken);
        var registry = new WorkflowRegistry().Register(
            "session-input",
            "1",
            new InputWorkflow());
        var engine = new WorkflowEngine(
            new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false
            }),
            registry);
        var runId = await engine.StartAsync(
            "session-input",
            "1",
            "start",
            cancellationToken: cancellationToken);
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, cancellationToken);
        var events = new SimingSessionEventStore(Path.Combine(root, "events"));
        var request = await events.AppendAsync(
            new SessionEventRequest(
                session.Id,
                "guyabano",
                SessionEventTypes.InputRequested,
                DateTimeOffset.UtcNow,
                CorrelationId: runId,
                CrossSystemRefs: new Dictionary<string, string>
                {
                    ["workflowRunId"] = runId.ToString("D"),
                    ["signalName"] = "clarification"
                },
                IdempotencyKey: $"input-request:{runId:D}:clarification"),
            cancellationToken);
        var service = new SessionWorkflowInputService(
            new FixedSessionWorkflowRuntimeProvider(engine),
            sessionStore,
            events,
            new FileSystemSessionDecisionLeaseProvider(Path.Combine(root, "leases")));
        return new Fixture(session, runId, request, engine, events, service);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed record Fixture(
        GuyabanoSession Session,
        Guid RunId,
        SessionEvent Request,
        WorkflowEngine Engine,
        SimingSessionEventStore Events,
        SessionWorkflowInputService Service);

    private sealed class InputWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken = default) =>
            context.WaitForSignalAsync<string>(
                "clarification-wait",
                "clarification",
                cancellationToken: cancellationToken);
    }
}
