using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;

namespace Guyabano.WorkflowProgressTests;

public sealed class SessionWorkflowFailureRecoveryTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-workflow-failure-recovery-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TerminalTimeout_RecordsDeterministicUserVisibleRecoveryChain()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = GuyabanoSessionId.New();
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"), projectionStore: projections);
        var service = new SessionWorkflowFailureRecoveryService(
            new SessionRecoveryCoordinator(events));
        var workflowEvent = new WorkflowEvent
        {
            Sequence = 9,
            WorkflowRunId = Guid.CreateVersion7(),
            EventType = WorkflowEventTypes.WorkflowFailed,
            Timestamp = DateTimeOffset.UtcNow,
            DataJson = "{\"exceptionType\":\"TimeoutException\",\"message\":\"step timed out\"}"
        };
        var mirrorEventId = Guid.CreateVersion7();

        await service.RecordAsync(sessionId, workflowEvent, mirrorEventId, ct);
        await service.RecordAsync(sessionId, workflowEvent, mirrorEventId, ct);

        var history = await events.ReadAsync(sessionId, cancellationToken: ct);
        history.Select(item => item.EventType).Should().Equal(
            SessionEventTypes.IncidentDetected,
            SessionEventTypes.RecoveryPlanned,
            SessionEventTypes.UserActionRequired);
        history[0].CausationId.Should().Be(mirrorEventId);
        history[0].CrossSystemRefs!["reasonCode"].Should().Be("WorkflowTimedOut");
        var projection = SessionTimelineProjection.Project(history);
        projection.OperatorState.Should().Be(SessionOperatorState.AwaitingInput);
        projection.OpenIncidentIds.Should().ContainSingle();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
