using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Penghou.Siming;
using Penghou.Siming.Sqlite;

namespace Guyabano.Session.Sqlite;

/// <summary>
/// Persists each Guyabano session in its own transactional Siming SQLite ledger
/// under <c>{root}/{session-id}/session.db</c>.
/// </summary>
public sealed class SimingSessionEventStore : ISessionEventStore, IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string rootPath;
    private readonly LedgerInputLimits inputLimits;
    private readonly ISessionProjectionStore? projectionStore;
    private readonly ConcurrentDictionary<GuyabanoSessionId, Lazy<SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>>> ledgers = new();
    private int disposed;

    /// <summary>Creates a session event store rooted at the supplied directory.</summary>
    public SimingSessionEventStore(
        string rootPath,
        LedgerInputLimits? inputLimits = null,
        ISessionProjectionStore? projectionStore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
        this.inputLimits = inputLimits ?? LedgerInputLimits.Default;
        this.projectionStore = projectionStore;
        this.inputLimits.Validate();
        Directory.CreateDirectory(this.rootPath);
    }

    /// <inheritdoc />
    public async Task<SessionEvent> AppendAsync(SessionEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EventType);
        if (!Enum.IsDefined(request.PayloadSensitivity))
            throw new ArgumentOutOfRangeException(nameof(request.PayloadSensitivity));
        if (!Enum.IsDefined(request.PayloadRetention))
            throw new ArgumentOutOfRangeException(nameof(request.PayloadRetention));
        var ledger = GetLedger(request.SessionId);
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await ledger.ReadByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                var replay = ResolveReplay(existing, request);
                await ApplyProjectionAsync(replay, cancellationToken).ConfigureAwait(false);
                return replay;
            }
        }

        var payload = SessionEventPayload.From(request, request.EventId ?? Guid.CreateVersion7());
        try
        {
            var entry = await ledger.AppendAsync(
                new LedgerAppendRequest<SessionEventPayload>(request.SessionId.ToString(), request.EventType, payload, request.IdempotencyKey),
                cancellationToken).ConfigureAwait(false);
            var committed = Map(entry);
            await ApplyProjectionAsync(committed, cancellationToken).ConfigureAwait(false);
            return committed;
        }
        catch (LedgerIdempotencyConflictException) when (request.IdempotencyKey is not null)
        {
            var existing = await ledger.ReadByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException(
                    $"Session event idempotency key '{request.IdempotencyKey}' conflicted but the committed entry could not be read.");
            var replay = ResolveReplay(existing, request);
            await ApplyProjectionAsync(replay, cancellationToken).ConfigureAwait(false);
            return replay;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionEvent>> ReadAsync(GuyabanoSessionId sessionId, long afterSequence = 0, CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var result = new List<SessionEvent>();
        var cursor = afterSequence;
        while (true)
        {
            var page = await ReadPageAsync(new SessionEventPageRequest(sessionId, cursor, SessionEventPageRequest.MaximumLimit), cancellationToken)
                .ConfigureAwait(false);
            result.AddRange(page.Events);
            if (!page.HasMore) return result;
            cursor = page.NextSequence!.Value;
        }
    }

    /// <inheritdoc />
    public async Task<SessionEventPage> ReadPageAsync(SessionEventPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var selected = new List<SessionEvent>(request.Limit + 1);
        await foreach (var entry in GetLedger(request.SessionId).ReadAsync(
            new LedgerReadRequest(AfterSequence: request.AfterSequence, Limit: request.Limit + 1), cancellationToken).ConfigureAwait(false))
            selected.Add(Map(entry));
        var hasMore = selected.Count > request.Limit;
        if (hasMore) selected.RemoveAt(selected.Count - 1);
        return new SessionEventPage(selected, hasMore ? selected[^1].Sequence : null, hasMore);
    }

    /// <inheritdoc />
    public async Task<SessionEvent?> VerifyChainAsync(GuyabanoSessionId sessionId, CancellationToken cancellationToken = default)
    {
        var ledger = GetLedger(sessionId);
        var verification = await ledger.VerifyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid)
            throw new InvalidOperationException($"Siming session ledger verification failed at sequence {verification.FailedSequence}: {verification.Failure} ({verification.Detail}).");
        if (verification.VerifiedEntries == 0) return null;
        var events = await ReadAsync(sessionId, Math.Max(0, verification.VerifiedHead.Sequence - 1), cancellationToken)
            .ConfigureAwait(false);
        return events.LastOrDefault();
    }

    /// <summary>Returns the deterministic ledger path for a session.</summary>
    public string GetLedgerPath(GuyabanoSessionId sessionId) =>
        Path.Combine(rootPath, sessionId.ToString(), "session.db");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var lazy in ledgers.Values)
            if (lazy.IsValueCreated) await lazy.Value.DisposeAsync().ConfigureAwait(false);
        ledgers.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer> GetLedger(GuyabanoSessionId sessionId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return ledgers.GetOrAdd(sessionId, id => new Lazy<SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>>(
            () => new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
                new SimingSqliteOptions { DatabasePath = GetLedgerPath(id), InputLimits = inputLimits },
                new CanonicalJsonPayloadSerializer(SerializerOptions)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private Task ApplyProjectionAsync(SessionEvent sessionEvent, CancellationToken cancellationToken) =>
        projectionStore?.ApplyAsync(sessionEvent, cancellationToken) ?? Task.CompletedTask;

    private static SessionEvent ResolveReplay(LedgerEntry entry, SessionEventRequest request)
    {
        var existing = Map(entry);
        if (!EquivalentRequest(existing, request))
            throw new InvalidOperationException($"Session event idempotency key '{request.IdempotencyKey}' is already used by a different event.");
        return existing;
    }

    private static SessionEvent Map(LedgerEntry entry)
    {
        var payload = JsonSerializer.Deserialize<SessionEventPayload>(entry.Payload.Span, SerializerOptions)
            ?? throw new InvalidDataException($"Session event payload at ledger sequence {entry.Sequence} is empty.");
        return new SessionEvent
        {
            SchemaVersion = payload.SchemaVersion,
            Sequence = entry.Sequence,
            EventId = payload.EventId,
            SessionId = GuyabanoSessionId.Parse(entry.StreamId),
            Actor = payload.Actor,
            EventType = entry.EventType,
            OccurredAt = payload.OccurredAt,
            CommittedAt = entry.CommittedAt,
            CausationId = payload.CausationId,
            CorrelationId = payload.CorrelationId,
            IdempotencyKey = payload.IdempotencyKey,
            CrossSystemRefs = payload.CrossSystemRefs,
            PayloadJson = payload.PayloadJson,
            PayloadSensitivity = payload.PayloadSensitivity,
            PayloadRetention = payload.PayloadRetention,
            PayloadDigest = payload.PayloadDigest,
            PreviousHash = entry.Sequence == 1 ? null : entry.PreviousHash.ToString(),
            Hash = entry.Hash.ToString()
        };
    }

    private static bool EquivalentRequest(SessionEvent existing, SessionEventRequest replay) =>
        existing.SessionId == replay.SessionId && existing.Actor == replay.Actor &&
        existing.EventType == replay.EventType && existing.CausationId == replay.CausationId &&
        existing.CorrelationId == replay.CorrelationId &&
        (replay.EventId is null || existing.EventId == replay.EventId) &&
        existing.PayloadSensitivity == replay.PayloadSensitivity &&
        existing.PayloadRetention == replay.PayloadRetention &&
        EquivalentReferences(existing.CrossSystemRefs, replay.CrossSystemRefs) &&
        EquivalentPayload(existing, replay);

    private static bool EquivalentPayload(SessionEvent existing, SessionEventRequest replay) =>
        replay.PayloadRetention switch
        {
            SessionPayloadRetention.Retain => existing.PayloadJson == replay.PayloadJson,
            SessionPayloadRetention.DigestOnly => existing.PayloadDigest == ComputePayloadDigest(replay.PayloadJson),
            SessionPayloadRetention.Omit => existing.PayloadJson is null && existing.PayloadDigest is null,
            _ => false
        };

    private static bool EquivalentReferences(IReadOnlyDictionary<string, string>? existing, IReadOnlyDictionary<string, string>? replay)
    {
        if (existing is null || replay is null) return existing is null && replay is null;
        return existing.Count == replay.Count && existing.All(item => replay.TryGetValue(item.Key, out var value) && value == item.Value);
    }

    private sealed record SessionEventPayload(
        int SchemaVersion,
        Guid EventId,
        string Actor,
        DateTimeOffset OccurredAt,
        Guid? CausationId,
        Guid? CorrelationId,
        string? IdempotencyKey,
        IReadOnlyDictionary<string, string>? CrossSystemRefs,
        string? PayloadJson,
        SessionPayloadSensitivity PayloadSensitivity,
        SessionPayloadRetention PayloadRetention,
        string? PayloadDigest)
    {
        public static SessionEventPayload From(SessionEventRequest request, Guid eventId) =>
            new(1, eventId, request.Actor, request.OccurredAt, request.CausationId, request.CorrelationId,
                request.IdempotencyKey, request.CrossSystemRefs,
                request.PayloadRetention == SessionPayloadRetention.Retain ? request.PayloadJson : null,
                request.PayloadSensitivity,
                request.PayloadRetention,
                request.PayloadRetention == SessionPayloadRetention.Omit
                    ? null
                    : ComputePayloadDigest(request.PayloadJson));
    }

    private static string? ComputePayloadDigest(string? payload) => payload is null
        ? null
        : $"sha256:utf8:v1:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}";
}
