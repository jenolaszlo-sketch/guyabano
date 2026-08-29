namespace Guyabano.Session;

public sealed record SessionCurrentState(
    int TotalEvents,
    string? LastEventType,
    DateTimeOffset? LastEventAt,
    IReadOnlyList<string> PendingInputEventIds,
    string? CurrentWorkspaceRevision,
    Guid? LastWorkflowRunId,
    DateTimeOffset? SessionCreatedAt,
    DateTimeOffset? LastCommittedAt = null,
    SessionOperatorState OperatorState = SessionOperatorState.Ready,
    IReadOnlyList<string>? OpenIncidentIds = null,
    int ResolvedIncidentCount = 0,
    string? LastIncidentReason = null);

public sealed record SessionProjectionSnapshot(
    GuyabanoSessionId SessionId,
    long AppliedSequence,
    string HeadHash,
    SessionCurrentState State);

/// <summary>
/// Durable delivery cursor comparing the authoritative Siming ledger head with
/// the rebuildable projection head.
/// </summary>
public sealed record SessionProjectionDeliveryStatus(
    GuyabanoSessionId SessionId,
    long CommittedSequence,
    string? CommittedHeadHash,
    long AppliedSequence,
    string? AppliedHeadHash,
    DateTimeOffset UpdatedAt,
    string? LastFailureType = null,
    string? LastFailureDetail = null)
{
    public bool IsLagging => AppliedSequence < CommittedSequence ||
        (AppliedSequence == CommittedSequence &&
         !string.Equals(AppliedHeadHash, CommittedHeadHash, StringComparison.Ordinal));
}

public interface ISessionProjectionDeliveryStore
{
    Task RecordCommittedAsync(
        SessionEvent sessionEvent,
        CancellationToken cancellationToken = default);

    Task RecordFailureAsync(
        SessionEvent sessionEvent,
        Exception exception,
        CancellationToken cancellationToken = default);

    Task<SessionProjectionDeliveryStatus?> GetDeliveryStatusAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionProjectionDeliveryStatus>> ListLaggingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default);
}

public interface ISessionProjectionStore
{
    Task ApplyAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default);

    Task<SessionProjectionSnapshot?> GetAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionProjectionSnapshot>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<SessionProjectionSnapshot?> RebuildAsync(
        GuyabanoSessionId sessionId,
        IReadOnlyList<SessionEvent> events,
        CancellationToken cancellationToken = default);
}

public static class SessionTimelineProjection
{
    public static SessionCurrentState Project(IReadOnlyList<SessionEvent> events)
    {
        SessionCurrentState? state = null;
        foreach (var sessionEvent in events.OrderBy(item => item.Sequence))
            state = Apply(state, sessionEvent);
        return state ?? new SessionCurrentState(0, null, null, [], null, null, null);
    }

    public static SessionCurrentState Apply(SessionCurrentState? state, SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        var pending = (state?.PendingInputEventIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        if (sessionEvent.EventType == SessionEventTypes.InputRequested)
            pending.Add(sessionEvent.EventId.ToString("D"));
        else if (sessionEvent.EventType == SessionEventTypes.InputProvided && sessionEvent.CausationId is not null)
            pending.Remove(sessionEvent.CausationId.Value.ToString("D"));

        var workspaceRevision = state?.CurrentWorkspaceRevision;
        if (sessionEvent.EventType == SessionEventTypes.WorkspacePromoted)
            workspaceRevision = sessionEvent.CrossSystemRefs?.GetValueOrDefault("toRevision") ?? workspaceRevision;
        var workflowRunId = state?.LastWorkflowRunId;
        if (sessionEvent.EventType is SessionEventTypes.WorkflowStarted or SessionEventTypes.WorkflowCompleted or SessionEventTypes.WorkflowFailed)
            workflowRunId = sessionEvent.CorrelationId ?? workflowRunId;

        var incidents = (state?.OpenIncidentIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var resolvedCount = state?.ResolvedIncidentCount ?? 0;
        var lastIncidentReason = state?.LastIncidentReason;
        var operatorState = state?.OperatorState ?? SessionOperatorState.Ready;
        var incidentId = sessionEvent.CrossSystemRefs?.GetValueOrDefault("incidentId");
        if (sessionEvent.EventType == SessionEventTypes.IncidentDetected && incidentId is not null)
        {
            incidents.Add(incidentId);
            lastIncidentReason = sessionEvent.CrossSystemRefs?.GetValueOrDefault("reasonCode");
            operatorState = string.Equals(
                sessionEvent.CrossSystemRefs?.GetValueOrDefault("severity"),
                SessionIncidentSeverity.Critical.ToString(),
                StringComparison.Ordinal)
                ? SessionOperatorState.Corrupt
                : SessionOperatorState.Recovering;
        }
        else if (sessionEvent.EventType == SessionEventTypes.RecoverySucceeded && incidentId is not null)
        {
            if (incidents.Remove(incidentId)) resolvedCount++;
            operatorState = incidents.Count == 0
                ? string.Equals(
                    sessionEvent.CrossSystemRefs?.GetValueOrDefault("recoveryAction"),
                    SessionRecoveryAction.RefreshPreview.ToString(),
                    StringComparison.Ordinal)
                    ? SessionOperatorState.AwaitingApproval
                    : SessionOperatorState.Ready
                : SessionOperatorState.Recovering;
        }
        else if (sessionEvent.EventType == SessionEventTypes.UserActionRequired)
        {
            operatorState = SessionOperatorState.AwaitingInput;
        }
        else if (sessionEvent.EventType == SessionEventTypes.RecoveryFailed)
        {
            operatorState = string.Equals(
                sessionEvent.CrossSystemRefs?.GetValueOrDefault("outcome"),
                SessionRecoveryOutcome.Corrupt.ToString(),
                StringComparison.Ordinal)
                ? SessionOperatorState.Corrupt
                : SessionOperatorState.ReconciliationRequired;
        }

        return new SessionCurrentState(
            (state?.TotalEvents ?? 0) + 1,
            sessionEvent.EventType,
            sessionEvent.OccurredAt,
            pending.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            workspaceRevision,
            workflowRunId,
            state?.SessionCreatedAt ?? sessionEvent.OccurredAt,
            sessionEvent.CommittedAt,
            operatorState,
            incidents.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            resolvedCount,
            lastIncidentReason);
    }

    /// <summary>
    /// Reconstructs a human- and machine-readable timeline of who did what, when,
    /// why (causation), and against which workflow/workspace revision.
    /// </summary>
    public static IReadOnlyList<string> RenderTimeline(IReadOnlyList<SessionEvent> events) =>
        events.Select(item =>
            $"[{item.OccurredAt:O}] #{item.Sequence} {item.EventType} by {item.Actor}" +
            (item.CausationId is not null ? $" caused-by {item.CausationId:D}" : string.Empty) +
            (item.CrossSystemRefs is { Count: > 0 }
                ? $" {string.Join(",", item.CrossSystemRefs.Select(pair => $"{pair.Key}={pair.Value}"))}"
                : string.Empty))
            .ToArray();
}
