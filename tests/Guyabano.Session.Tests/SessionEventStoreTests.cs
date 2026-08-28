using FluentAssertions;
using Guyabano.Session;

namespace Guyabano.SessionTests;

public sealed class SessionEventStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-session-event-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Append_OrdersSequencesAndHashChains()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new FileSystemSessionEventStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        var correlation = Guid.NewGuid();

        var first = await store.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow,
            CorrelationId: correlation, PayloadJson: "{\"prompt\":\"hello\"}"), ct);
        var second = await store.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.WorkflowStarted, DateTimeOffset.UtcNow,
            CorrelationId: correlation, CausationId: first.EventId), ct);

        first.Sequence.Should().Be(1);
        second.Sequence.Should().Be(2);
        first.PreviousHash.Should().BeNull();
        second.PreviousHash.Should().Be(first.Hash);
        first.Hash.Should().NotBe(second.Hash);

        var all = await store.ReadAsync(sessionId, cancellationToken: ct);
        all.Should().HaveCount(2);
        all[0].Sequence.Should().Be(1);
        all[1].Sequence.Should().Be(2);
        all[1].CausationId.Should().Be(first.EventId);

        var last = await store.VerifyChainAsync(sessionId, ct);
        last!.Sequence.Should().Be(2);
    }

    [Fact]
    public async Task VerifyChain_DetectsTampering()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new FileSystemSessionEventStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.WorkflowStarted, DateTimeOffset.UtcNow), ct);

        // Tamper with the first event's actor in the file
        var path = Path.Combine(rootPath, sessionId.ToString(), "events.jsonl");
        var lines = await File.ReadAllLinesAsync(path, ct);
        var tampered = lines[0].Replace("\"actor\":\"user\"", "\"actor\":\"attacker\"");
        await File.WriteAllLinesAsync(path, [tampered, .. lines.Skip(1)], ct);

        var act = () => store.VerifyChainAsync(sessionId, ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Projection_TracksPendingInputsWorkspaceRevisionAndLastWorkflow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new FileSystemSessionEventStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        var workflowRun = Guid.NewGuid();
        var inputEvent = await store.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.InputRequested, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun), ct);
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.WorkflowStarted, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun), ct);
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.WorkspacePromoted, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun,
            CrossSystemRefs: new Dictionary<string, string>
            {
                ["toRevision"] = "rev-abc"
            }), ct);

        var projection = SessionTimelineProjection.Project(
            await store.ReadAsync(sessionId, cancellationToken: ct));
        projection.TotalEvents.Should().Be(3);
        projection.PendingInputEventIds.Should().Equal(inputEvent.EventId.ToString("D"));
        projection.CurrentWorkspaceRevision.Should().Be("rev-abc");
        projection.LastWorkflowRunId.Should().Be(workflowRun);

        // Provide the input: pending resolves
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.InputProvided, DateTimeOffset.UtcNow,
            CausationId: inputEvent.EventId, CorrelationId: workflowRun), ct);
        var projectionAfter = SessionTimelineProjection.Project(
            await store.ReadAsync(sessionId, cancellationToken: ct));
        projectionAfter.PendingInputEventIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconstructsWhoWhatWhenWhyAfterRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new FileSystemSessionEventStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        var workflowRun = Guid.NewGuid();

        var userMessage = await store.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun, PayloadJson: "{\"prompt\":\"add auth\"}"), ct);
        var started = await store.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.WorkflowStarted, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun, CausationId: userMessage.EventId), ct);
        var approval = await store.AppendAsync(new SessionEventRequest(
            sessionId, "tester", SessionEventTypes.ApprovalGranted, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun, CausationId: started.EventId,
            CrossSystemRefs: new Dictionary<string, string>
            {
                ["targetStepKey"] = "generation/task-1/leaf-1",
                ["workflowRunId"] = workflowRun.ToString("D")
            }), ct);
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.RestartApplied, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun, CausationId: approval.EventId,
            CrossSystemRefs: new Dictionary<string, string>
            {
                ["targetStepKey"] = "generation/task-1/leaf-1"
            }), ct);

        var events = await store.ReadAsync(sessionId, cancellationToken: ct);
        var timeline = SessionTimelineProjection.RenderTimeline(events);
        timeline.Should().HaveCount(4);
        timeline[3].Should().Contain("restart-applied by guyabano");
        timeline[3].Should().Contain($"caused-by {approval.EventId:D}");
        timeline[3].Should().Contain("targetStepKey=generation/task-1/leaf-1");

        // Why: the restart was caused by tester's approval, which was caused by workflow start, caused by user message
        var restart = events[3];
        var whyApproval = events.Single(e => e.EventId == restart.CausationId);
        whyApproval.Actor.Should().Be("tester");
        whyApproval.EventType.Should().Be(SessionEventTypes.ApprovalGranted);
        var whyStart = events.Single(e => e.EventId == whyApproval.CausationId);
        whyStart.EventType.Should().Be(SessionEventTypes.WorkflowStarted);
        var whyUser = events.Single(e => e.EventId == whyStart.CausationId);
        whyUser.Actor.Should().Be("user");
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
