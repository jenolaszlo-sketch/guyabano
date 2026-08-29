using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Microsoft.Data.Sqlite;

namespace Guyabano.SessionTests;

public sealed class SessionRecoveryCoordinatorTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(), "guyabano-recovery-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FailedThenSuccessfulRecovery_PreservesEveryAttemptAndReturnsReady()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(Path.Combine(rootPath, "catalog.db"));
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"), projectionStore: projections);
        var coordinator = new SessionRecoveryCoordinator(events);
        var sessionId = GuyabanoSessionId.New();
        var incidentId = Guid.CreateVersion7();
        var planId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var detected = await coordinator.DetectAsync(new SessionIncident(
            incidentId,
            sessionId,
            "ParticipantUnavailable",
            SessionIncidentSeverity.Error,
            "Hetu publication was temporarily unavailable.",
            now), ct);
        var plan = new SessionRecoveryPlan(
            planId,
            incidentId,
            sessionId,
            SessionRecoveryAction.RetryIdempotently,
            "Retry the idempotent Hetu publication.",
            "workspace-7",
            Automatic: true,
            PlannedAt: now);
        var planned = await coordinator.PlanAsync(plan, detected.EventId, ct);
        var firstAttempt = await coordinator.RecordAttemptAsync(plan, planned.EventId, 1, ct);
        await coordinator.CompleteAsync(new SessionRecoveryResolution(
            planId,
            incidentId,
            sessionId,
            SessionRecoveryOutcome.ReconciliationRequired,
            1,
            "The first retry failed; the accepted workspace remains unchanged.",
            now), firstAttempt.EventId, ct);

        var failedState = await projections.GetAsync(sessionId, ct);
        failedState!.State.OperatorState.Should().Be(SessionOperatorState.ReconciliationRequired);
        failedState.State.OpenIncidentIds.Should().ContainSingle(incidentId.ToString("D"));

        var secondAttempt = await coordinator.RecordAttemptAsync(plan, planned.EventId, 2, ct);
        await coordinator.CompleteAsync(new SessionRecoveryResolution(
            planId,
            incidentId,
            sessionId,
            SessionRecoveryOutcome.Recovered,
            2,
            "The second retry succeeded.",
            now.AddSeconds(1)), secondAttempt.EventId, ct);

        var recoveredState = await projections.GetAsync(sessionId, ct);
        recoveredState!.State.OperatorState.Should().Be(SessionOperatorState.Ready);
        recoveredState.State.OpenIncidentIds.Should().BeEmpty();
        recoveredState.State.ResolvedIncidentCount.Should().Be(1);
        var history = await events.ReadAsync(sessionId, cancellationToken: ct);
        history.Select(item => item.EventType).Should().Equal(
            SessionEventTypes.IncidentDetected,
            SessionEventTypes.RecoveryPlanned,
            SessionEventTypes.RecoveryAttempted,
            SessionEventTypes.RecoveryFailed,
            SessionEventTypes.RecoveryAttempted,
            SessionEventTypes.RecoverySucceeded);
        history.Select(item => item.CausationId).Skip(1).Should().OnlyContain(id => id.HasValue);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }
}
