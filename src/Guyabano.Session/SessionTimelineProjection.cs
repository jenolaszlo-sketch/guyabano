namespace Guyabano.Session;

public sealed record SessionCurrentState(
    int TotalEvents,
    string? LastEventType,
    DateTimeOffset? LastEventAt,
    IReadOnlyList<string> PendingInputEventIds,
    string? CurrentWorkspaceRevision,
    Guid? LastWorkflowRunId,
    DateTimeOffset? SessionCreatedAt);

public static class SessionTimelineProjection
{
    public static SessionCurrentState Project(IReadOnlyList<SessionEvent> events)
    {
        var pending = events
            .Where(item => item.EventType == SessionEventTypes.InputRequested)
            .Select(item => item.EventId.ToString("D"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (item.EventType == SessionEventTypes.InputProvided &&
                item.CausationId is not null)
            {
                pending.Remove(item.CausationId.Value.ToString("D"));
            }
        }

        var last = events.LastOrDefault();
        var lastWorkflowRun = events
            .Where(item => item.EventType is SessionEventTypes.WorkflowStarted or
                SessionEventTypes.WorkflowCompleted or
                SessionEventTypes.WorkflowFailed)
            .Select(item => item.CorrelationId)
            .LastOrDefault(item => item is not null);
        var promotion = events
            .Where(item => item.EventType == SessionEventTypes.WorkspacePromoted)
            .Select(item => item.CrossSystemRefs?.GetValueOrDefault("toRevision"))
            .LastOrDefault(revision => revision is not null);

        return new SessionCurrentState(
            TotalEvents: events.Count,
            LastEventType: last?.EventType,
            LastEventAt: last?.OccurredAt,
            PendingInputEventIds: pending.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            CurrentWorkspaceRevision: promotion,
            LastWorkflowRunId: lastWorkflowRun,
            SessionCreatedAt: events.FirstOrDefault()?.OccurredAt);
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
