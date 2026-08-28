namespace Guyabano.Session;

public interface ISessionEventStore
{
    /// <summary>Appends an event and returns the durable envelope with assigned sequence/hash.</summary>
    Task<SessionEvent> AppendAsync(
        SessionEventRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads events after an optional sequence in ascending order.</summary>
    Task<IReadOnlyList<SessionEvent>> ReadAsync(
        GuyabanoSessionId sessionId,
        long afterSequence = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Validates the append-only hash chain and returns the last event (or null).</summary>
    Task<SessionEvent?> VerifyChainAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default);
}

public sealed record SessionEventRequest(
    GuyabanoSessionId SessionId,
    string Actor,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? CausationId = null,
    Guid? CorrelationId = null,
    IReadOnlyDictionary<string, string>? CrossSystemRefs = null,
    string? PayloadJson = null);
