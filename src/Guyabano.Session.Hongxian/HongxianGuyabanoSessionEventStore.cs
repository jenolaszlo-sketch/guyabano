using Penghou.Hongxian;
using HongxianEvents = Penghou.Hongxian.ISessionEventStore;

namespace Guyabano.Session.Hongxian;

/// <summary>
/// Guyabano-owned mapping from the product event vocabulary onto the reusable
/// Hongxian ledger. Event types pass through verbatim as application-defined
/// events; the free-form Guyabano actor becomes a legacy participant claim
/// namespaced to Guyabano, so the round trip is exact. Sensitivity and
/// retention enums are numerically identical on both sides and mapped
/// explicitly.
/// </summary>
public sealed class HongxianGuyabanoSessionEventStore(HongxianEvents inner) : ISessionEventStore
{
    public async Task<SessionEvent> AppendAsync(
        SessionEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var appended = await inner.AppendAsync(
            new Penghou.Hongxian.SessionEventRequest(
                new Penghou.Hongxian.SessionId(request.SessionId.Value),
                new SessionParticipantAttribution(
                    SessionParticipantKinds.Legacy,
                    "guyabano",
                    request.Actor),
                request.EventType,
                request.OccurredAt,
                request.CausationId,
                request.CorrelationId,
                request.CrossSystemRefs,
                request.PayloadJson,
                request.IdempotencyKey,
                request.EventId,
                Map(request.PayloadSensitivity),
                Map(request.PayloadRetention)),
            cancellationToken).ConfigureAwait(false);
        return Map(appended);
    }

    public async Task<IReadOnlyList<SessionEvent>> ReadAsync(
        GuyabanoSessionId sessionId,
        long afterSequence = 0,
        CancellationToken cancellationToken = default)
    {
        var events = await inner.ReadAsync(
            new Penghou.Hongxian.SessionId(sessionId.Value), afterSequence, cancellationToken).ConfigureAwait(false);
        return events.Select(Map).ToArray();
    }

    public async Task<SessionEventPage> ReadPageAsync(
        SessionEventPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = await inner.ReadPageAsync(
            new Penghou.Hongxian.SessionEventPageRequest(
                new Penghou.Hongxian.SessionId(request.SessionId.Value), request.AfterSequence, request.Limit),
            cancellationToken).ConfigureAwait(false);
        return new SessionEventPage(page.Events.Select(Map).ToArray(), page.NextSequence, page.HasMore);
    }

    public async Task<SessionEvent?> VerifyChainAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var last = await inner.VerifyChainAsync(
            new Penghou.Hongxian.SessionId(sessionId.Value), cancellationToken).ConfigureAwait(false);
        return last is null ? null : Map(last);
    }

    private static SessionEvent Map(Penghou.Hongxian.SessionEvent source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Sequence = source.Sequence,
        EventId = source.EventId,
        SessionId = new GuyabanoSessionId(source.SessionId.Value),
        Actor = Map(source.Participant),
        EventType = source.EventType,
        OccurredAt = source.OccurredAt,
        CommittedAt = source.CommittedAt,
        CausationId = source.CausationId,
        CorrelationId = source.CorrelationId,
        IdempotencyKey = source.IdempotencyKey,
        CrossSystemRefs = source.CrossSystemRefs,
        PayloadJson = source.PayloadJson,
        PayloadSensitivity = Map(source.PayloadSensitivity),
        PayloadRetention = Map(source.PayloadRetention),
        PayloadDigest = source.PayloadDigest,
        PreviousHash = source.PreviousHash,
        Hash = source.Hash,
    };

    private static string Map(SessionParticipantAttribution participant) =>
        string.Equals(participant.Provider, "guyabano", StringComparison.Ordinal) &&
        string.Equals(participant.Kind, SessionParticipantKinds.Legacy, StringComparison.Ordinal)
            ? participant.Subject
            : participant.ToString();

    private static Penghou.Hongxian.SessionPayloadSensitivity Map(SessionPayloadSensitivity value) =>
        value switch
        {
            SessionPayloadSensitivity.Public => Penghou.Hongxian.SessionPayloadSensitivity.Public,
            SessionPayloadSensitivity.Internal => Penghou.Hongxian.SessionPayloadSensitivity.Internal,
            SessionPayloadSensitivity.Confidential => Penghou.Hongxian.SessionPayloadSensitivity.Confidential,
            SessionPayloadSensitivity.Restricted => Penghou.Hongxian.SessionPayloadSensitivity.Restricted,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static SessionPayloadSensitivity Map(Penghou.Hongxian.SessionPayloadSensitivity value) =>
        value switch
        {
            Penghou.Hongxian.SessionPayloadSensitivity.Public => SessionPayloadSensitivity.Public,
            Penghou.Hongxian.SessionPayloadSensitivity.Internal => SessionPayloadSensitivity.Internal,
            Penghou.Hongxian.SessionPayloadSensitivity.Confidential => SessionPayloadSensitivity.Confidential,
            Penghou.Hongxian.SessionPayloadSensitivity.Restricted => SessionPayloadSensitivity.Restricted,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static Penghou.Hongxian.SessionPayloadRetention Map(SessionPayloadRetention value) =>
        value switch
        {
            SessionPayloadRetention.Retain => Penghou.Hongxian.SessionPayloadRetention.Retain,
            SessionPayloadRetention.DigestOnly => Penghou.Hongxian.SessionPayloadRetention.DigestOnly,
            SessionPayloadRetention.Omit => Penghou.Hongxian.SessionPayloadRetention.Omit,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static SessionPayloadRetention Map(Penghou.Hongxian.SessionPayloadRetention value) =>
        value switch
        {
            Penghou.Hongxian.SessionPayloadRetention.Retain => SessionPayloadRetention.Retain,
            Penghou.Hongxian.SessionPayloadRetention.DigestOnly => SessionPayloadRetention.DigestOnly,
            Penghou.Hongxian.SessionPayloadRetention.Omit => SessionPayloadRetention.Omit,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
}
