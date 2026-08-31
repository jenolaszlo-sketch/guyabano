namespace Guyabano.Session;

public sealed record SessionPendingInput(
    Guid RequestEventId,
    Guid? WorkflowRunId,
    string? SignalName,
    DateTimeOffset RequestedAt,
    DateTimeOffset ClaimedOccurredAt);

public sealed record SessionPendingApproval(
    Guid PreviewId,
    Guid? WorkflowRunId,
    string? TargetStepKey,
    string? WorkspaceRevision,
    DateTimeOffset PreviewedAt,
    DateTimeOffset ClaimedOccurredAt);

public sealed record SessionActiveIncident(
    Guid IncidentId,
    string? ReasonCode,
    SessionIncidentSeverity Severity,
    DateTimeOffset DetectedAt,
    DateTimeOffset ClaimedOccurredAt,
    Guid? WorkflowRunId = null,
    SessionRecoveryOutcome? Outcome = null);

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
    string? LastIncidentReason = null,
    IReadOnlyList<SessionPendingInput>? PendingInputs = null,
    IReadOnlyList<SessionPendingApproval>? PendingApprovals = null,
    IReadOnlyList<SessionActiveIncident>? ActiveIncidents = null);

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
        var pendingInputs = RestorePendingInputs(state);
        if (sessionEvent.EventType == SessionEventTypes.InputRequested)
        {
            pendingInputs[sessionEvent.EventId] = new SessionPendingInput(
                sessionEvent.EventId,
                sessionEvent.CorrelationId,
                Reference(sessionEvent, "signalName"),
                sessionEvent.CommittedAt,
                sessionEvent.OccurredAt);
        }
        else if (sessionEvent.EventType == SessionEventTypes.InputProvided && sessionEvent.CausationId is not null)
            pendingInputs.Remove(sessionEvent.CausationId.Value);

        var pendingApprovals = (state?.PendingApprovals ?? [])
            .ToDictionary(item => item.PreviewId);
        if (sessionEvent.EventType == SessionEventTypes.InvalidationPreviewed &&
            TryReferenceGuid(sessionEvent, "previewId", out var previewId))
        {
            pendingApprovals[previewId] = new SessionPendingApproval(
                previewId,
                ReferenceGuid(sessionEvent, "workflowRunId") ?? sessionEvent.CorrelationId,
                Reference(sessionEvent, "targetStepKey"),
                Reference(sessionEvent, "workspaceRevision"),
                sessionEvent.CommittedAt,
                sessionEvent.OccurredAt);
        }
        else if (sessionEvent.EventType is SessionEventTypes.ApprovalGranted or
                 SessionEventTypes.ApprovalDenied or
                 SessionEventTypes.PreviewSuperseded &&
                 TryReferenceGuid(sessionEvent, "previewId", out previewId))
        {
            pendingApprovals.Remove(previewId);
        }

        var workspaceRevision = state?.CurrentWorkspaceRevision;
        if (sessionEvent.EventType == SessionEventTypes.WorkspacePromoted)
            workspaceRevision = sessionEvent.CrossSystemRefs?.GetValueOrDefault("toRevision") ?? workspaceRevision;
        var workflowRunId = state?.LastWorkflowRunId;
        if (sessionEvent.EventType is SessionEventTypes.WorkflowStarted or SessionEventTypes.WorkflowCompleted or SessionEventTypes.WorkflowFailed)
            workflowRunId = sessionEvent.CorrelationId ?? workflowRunId;

        var incidents = RestoreIncidents(state);
        var resolvedCount = state?.ResolvedIncidentCount ?? 0;
        var lastIncidentReason = state?.LastIncidentReason;
        var incidentId = ReferenceGuid(sessionEvent, "incidentId");
        if (sessionEvent.EventType == SessionEventTypes.IncidentDetected && incidentId is not null)
        {
            lastIncidentReason = Reference(sessionEvent, "reasonCode");
            incidents[incidentId.Value] = new SessionActiveIncident(
                incidentId.Value,
                lastIncidentReason,
                ParseEnum(Reference(sessionEvent, "severity"), SessionIncidentSeverity.Error),
                sessionEvent.CommittedAt,
                sessionEvent.OccurredAt,
                sessionEvent.CorrelationId);
        }
        else if (sessionEvent.EventType == SessionEventTypes.RecoverySucceeded && incidentId is not null)
        {
            if (incidents.Remove(incidentId.Value)) resolvedCount++;
        }
        else if (sessionEvent.EventType is SessionEventTypes.UserActionRequired or SessionEventTypes.RecoveryFailed &&
                 incidentId is not null && incidents.TryGetValue(incidentId.Value, out var incident))
        {
            incidents[incidentId.Value] = incident with
            {
                Outcome = sessionEvent.EventType == SessionEventTypes.UserActionRequired
                    ? SessionRecoveryOutcome.UserActionRequired
                    : ParseEnum(
                        Reference(sessionEvent, "outcome"),
                        SessionRecoveryOutcome.ReconciliationRequired)
            };
        }

        var orderedInputs = pendingInputs.Values
            .OrderBy(item => item.RequestEventId)
            .ToArray();
        var orderedApprovals = pendingApprovals.Values
            .OrderBy(item => item.PreviewId)
            .ToArray();
        var orderedIncidents = incidents.Values
            .OrderBy(item => item.IncidentId)
            .ToArray();
        var operatorState = DeriveOperatorState(orderedInputs, orderedApprovals, orderedIncidents);

        return new SessionCurrentState(
            (state?.TotalEvents ?? 0) + 1,
            sessionEvent.EventType,
            sessionEvent.CommittedAt,
            orderedInputs.Select(item => item.RequestEventId.ToString("D")).ToArray(),
            workspaceRevision,
            workflowRunId,
            state?.SessionCreatedAt ?? sessionEvent.CommittedAt,
            sessionEvent.CommittedAt,
            operatorState,
            orderedIncidents.Select(item => item.IncidentId.ToString("D")).ToArray(),
            resolvedCount,
            lastIncidentReason,
            orderedInputs,
            orderedApprovals,
            orderedIncidents);
    }

    /// <summary>
    /// Reconstructs a human- and machine-readable timeline of who did what, when,
    /// why (causation), and against which workflow/workspace revision.
    /// </summary>
    public static IReadOnlyList<string> RenderTimeline(IReadOnlyList<SessionEvent> events) =>
        events.Select(item =>
            $"[{item.CommittedAt:O}] #{item.Sequence} {item.EventType} by {item.Actor}" +
            (item.OccurredAt != item.CommittedAt ? $" claimed-at {item.OccurredAt:O}" : string.Empty) +
            (item.CausationId is not null ? $" caused-by {item.CausationId:D}" : string.Empty) +
            (item.CrossSystemRefs is { Count: > 0 }
                ? $" {string.Join(",", item.CrossSystemRefs.Select(pair => $"{pair.Key}={pair.Value}"))}"
                : string.Empty))
            .ToArray();

    private static Dictionary<Guid, SessionPendingInput> RestorePendingInputs(
        SessionCurrentState? state)
    {
        if (state?.PendingInputs is not null)
            return state.PendingInputs.ToDictionary(item => item.RequestEventId);

        return (state?.PendingInputEventIds ?? [])
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
            .Where(id => id is not null)
            .ToDictionary(
                id => id!.Value,
                id => new SessionPendingInput(
                    id!.Value,
                    state?.LastWorkflowRunId,
                    null,
                    state?.LastCommittedAt ?? DateTimeOffset.MinValue,
                    state?.LastEventAt ?? DateTimeOffset.MinValue));
    }

    private static Dictionary<Guid, SessionActiveIncident> RestoreIncidents(
        SessionCurrentState? state)
    {
        if (state?.ActiveIncidents is not null)
            return state.ActiveIncidents.ToDictionary(item => item.IncidentId);

        return (state?.OpenIncidentIds ?? [])
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
            .Where(id => id is not null)
            .ToDictionary(
                id => id!.Value,
                id => new SessionActiveIncident(
                    id!.Value,
                    state?.LastIncidentReason,
                    state?.OperatorState == SessionOperatorState.Corrupt
                        ? SessionIncidentSeverity.Critical
                        : SessionIncidentSeverity.Error,
                    state?.LastCommittedAt ?? DateTimeOffset.MinValue,
                    state?.LastEventAt ?? DateTimeOffset.MinValue,
                    state?.LastWorkflowRunId,
                    state?.OperatorState switch
                    {
                        SessionOperatorState.AwaitingInput => SessionRecoveryOutcome.UserActionRequired,
                        SessionOperatorState.ReconciliationRequired => SessionRecoveryOutcome.ReconciliationRequired,
                        SessionOperatorState.Corrupt => SessionRecoveryOutcome.Corrupt,
                        _ => null
                    }));
    }

    private static SessionOperatorState DeriveOperatorState(
        IReadOnlyList<SessionPendingInput> pendingInputs,
        IReadOnlyList<SessionPendingApproval> pendingApprovals,
        IReadOnlyList<SessionActiveIncident> incidents)
    {
        if (incidents.Any(item => item.Severity == SessionIncidentSeverity.Critical ||
                                  item.Outcome == SessionRecoveryOutcome.Corrupt))
            return SessionOperatorState.Corrupt;
        if (incidents.Any(item => item.Outcome == SessionRecoveryOutcome.ReconciliationRequired))
            return SessionOperatorState.ReconciliationRequired;
        if (pendingInputs.Count > 0 ||
            incidents.Any(item => item.Outcome == SessionRecoveryOutcome.UserActionRequired))
            return SessionOperatorState.AwaitingInput;
        if (pendingApprovals.Count > 0)
            return SessionOperatorState.AwaitingApproval;
        return incidents.Count > 0
            ? SessionOperatorState.Recovering
            : SessionOperatorState.Ready;
    }

    private static string? Reference(SessionEvent sessionEvent, string name) =>
        sessionEvent.CrossSystemRefs?.GetValueOrDefault(name);

    private static Guid? ReferenceGuid(SessionEvent sessionEvent, string name) =>
        TryReferenceGuid(sessionEvent, name, out var value) ? value : null;

    private static bool TryReferenceGuid(SessionEvent sessionEvent, string name, out Guid value) =>
        Guid.TryParse(Reference(sessionEvent, name), out value);

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
