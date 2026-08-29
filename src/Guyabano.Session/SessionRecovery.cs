using System.Text.Json;

namespace Guyabano.Session;

public enum SessionIncidentSeverity
{
    Warning,
    Error,
    Critical
}

public enum SessionRecoveryAction
{
    None,
    RefreshPreview,
    AbandonCandidate,
    RetryIdempotently,
    ReconcileForward,
    HaltMutation
}

public enum SessionRecoveryOutcome
{
    Recovered,
    UserActionRequired,
    ReconciliationRequired,
    Corrupt
}

public enum SessionOperatorState
{
    Ready,
    AwaitingInput,
    AwaitingApproval,
    Recovering,
    ReconciliationRequired,
    Corrupt
}

public sealed record SessionIncident(
    Guid IncidentId,
    GuyabanoSessionId SessionId,
    string ReasonCode,
    SessionIncidentSeverity Severity,
    string Summary,
    DateTimeOffset DetectedAt,
    Guid? CorrelationId = null,
    IReadOnlyDictionary<string, string>? CrossSystemRefs = null,
    Guid? CausationId = null);

public sealed record SessionRecoveryPlan(
    Guid RecoveryPlanId,
    Guid IncidentId,
    GuyabanoSessionId SessionId,
    SessionRecoveryAction Action,
    string Explanation,
    string? SafeWorkspaceRevision,
    bool Automatic,
    DateTimeOffset PlannedAt,
    Guid? CorrelationId = null,
    IReadOnlyDictionary<string, string>? CrossSystemRefs = null);

public sealed record SessionRecoveryResolution(
    Guid RecoveryPlanId,
    Guid IncidentId,
    GuyabanoSessionId SessionId,
    SessionRecoveryOutcome Outcome,
    int Attempt,
    string Explanation,
    DateTimeOffset CompletedAt,
    Guid? CorrelationId = null,
    IReadOnlyDictionary<string, string>? CrossSystemRefs = null);

/// <summary>
/// Appends forward-only incident and recovery evidence. It never changes the
/// operation or workflow that produced the incident.
/// </summary>
public sealed class SessionRecoveryCoordinator(ISessionEventStore events)
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public Task<SessionEvent> DetectAsync(
        SessionIncident incident,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentException.ThrowIfNullOrWhiteSpace(incident.ReasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(incident.Summary);
        return events.AppendAsync(new SessionEventRequest(
            incident.SessionId,
            "guyabano",
            SessionEventTypes.IncidentDetected,
            incident.DetectedAt,
            CausationId: incident.CausationId,
            CorrelationId: incident.CorrelationId,
            CrossSystemRefs: References(
                incident.CrossSystemRefs,
                ("incidentId", incident.IncidentId.ToString("D")),
                ("reasonCode", incident.ReasonCode),
                ("severity", incident.Severity.ToString())),
            PayloadJson: JsonSerializer.Serialize(incident, SerializerOptions),
            IdempotencyKey: $"incident:{incident.IncidentId:D}:detected"),
            cancellationToken);
    }

    public Task<SessionEvent> PlanAsync(
        SessionRecoveryPlan plan,
        Guid detectedEventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Explanation);
        return events.AppendAsync(new SessionEventRequest(
            plan.SessionId,
            "guyabano",
            SessionEventTypes.RecoveryPlanned,
            plan.PlannedAt,
            CausationId: detectedEventId,
            CorrelationId: plan.CorrelationId,
            CrossSystemRefs: References(
                plan.CrossSystemRefs,
                ("incidentId", plan.IncidentId.ToString("D")),
                ("recoveryPlanId", plan.RecoveryPlanId.ToString("D")),
                ("recoveryAction", plan.Action.ToString()),
                ("automatic", plan.Automatic.ToString()),
                ("safeWorkspaceRevision", plan.SafeWorkspaceRevision)),
            PayloadJson: JsonSerializer.Serialize(plan, SerializerOptions),
            IdempotencyKey: $"incident:{plan.IncidentId:D}:plan:{plan.RecoveryPlanId:D}"),
            cancellationToken);
    }

    public Task<SessionEvent> RecordAttemptAsync(
        SessionRecoveryPlan plan,
        Guid plannedEventId,
        int attempt,
        CancellationToken cancellationToken = default)
    {
        if (attempt <= 0) throw new ArgumentOutOfRangeException(nameof(attempt));
        return events.AppendAsync(new SessionEventRequest(
            plan.SessionId,
            "guyabano",
            SessionEventTypes.RecoveryAttempted,
            DateTimeOffset.UtcNow,
            CausationId: plannedEventId,
            CorrelationId: plan.CorrelationId,
            CrossSystemRefs: References(
                plan.CrossSystemRefs,
                ("incidentId", plan.IncidentId.ToString("D")),
                ("recoveryPlanId", plan.RecoveryPlanId.ToString("D")),
                ("recoveryAction", plan.Action.ToString()),
                ("attempt", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            IdempotencyKey: $"incident:{plan.IncidentId:D}:plan:{plan.RecoveryPlanId:D}:attempt:{attempt}"),
            cancellationToken);
    }

    public Task<SessionEvent> CompleteAsync(
        SessionRecoveryResolution resolution,
        Guid causationEventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Attempt < 0 ||
            resolution.Attempt == 0 &&
            resolution.Outcome != SessionRecoveryOutcome.UserActionRequired)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolution.Attempt),
                "Attempt zero is reserved for recovery actions that were explicitly deferred to a user.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution.Explanation);
        var eventType = resolution.Outcome switch
        {
            SessionRecoveryOutcome.Recovered => SessionEventTypes.RecoverySucceeded,
            SessionRecoveryOutcome.UserActionRequired => SessionEventTypes.UserActionRequired,
            _ => SessionEventTypes.RecoveryFailed
        };
        return events.AppendAsync(new SessionEventRequest(
            resolution.SessionId,
            "guyabano",
            eventType,
            resolution.CompletedAt,
            CausationId: causationEventId,
            CorrelationId: resolution.CorrelationId,
            CrossSystemRefs: References(
                resolution.CrossSystemRefs,
                ("incidentId", resolution.IncidentId.ToString("D")),
                ("recoveryPlanId", resolution.RecoveryPlanId.ToString("D")),
                ("attempt", resolution.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("outcome", resolution.Outcome.ToString())),
            PayloadJson: JsonSerializer.Serialize(resolution, SerializerOptions),
            IdempotencyKey: $"incident:{resolution.IncidentId:D}:plan:{resolution.RecoveryPlanId:D}:attempt:{resolution.Attempt}:outcome"),
            cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> References(
        IReadOnlyDictionary<string, string>? source,
        params (string Key, string? Value)[] additions)
    {
        var result = source is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source, StringComparer.Ordinal);
        foreach (var (key, value) in additions)
            if (!string.IsNullOrWhiteSpace(value)) result[key] = value;
        return result;
    }
}
