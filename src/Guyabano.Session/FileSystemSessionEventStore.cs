using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guyabano.Session;

/// <summary>
/// Append-only, ordered, hash-chained session event store backed by one JSONL file
/// per session. Appends are serialized under a per-session gate; each event's hash
/// covers its content plus the previous event's hash for tamper evidence.
/// </summary>
public sealed class FileSystemSessionEventStore : ISessionEventStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string rootPath;
    private readonly Dictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gatesGate = new(1, 1);

    public FileSystemSessionEventStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(this.rootPath);
    }

    public async Task<SessionEvent> AppendAsync(
        SessionEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EventType);

        var path = EventPath(request.SessionId);
        var gate = await GetGateAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var events = File.Exists(path)
                ? await ReadAllAsync(path, cancellationToken).ConfigureAwait(false)
                : [];
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                var replay = events.SingleOrDefault(item => string.Equals(
                    item.IdempotencyKey,
                    request.IdempotencyKey,
                    StringComparison.Ordinal));
                if (replay is not null)
                {
                    if (!EquivalentRequest(replay, request))
                        throw new InvalidOperationException(
                            $"Session event idempotency key '{request.IdempotencyKey}' is already used by a different event.");
                    return replay;
                }
            }
            var last = events.LastOrDefault();
            var previousHash = last?.Hash;
            var sequence = (last?.Sequence ?? 0) + 1;
            var eventId = Guid.CreateVersion7();
            var envelope = new SessionEvent
            {
                Sequence = sequence,
                EventId = eventId,
                SessionId = request.SessionId,
                Actor = request.Actor,
                EventType = request.EventType,
                OccurredAt = request.OccurredAt,
                CausationId = request.CausationId,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey,
                CrossSystemRefs = request.CrossSystemRefs,
                PayloadJson = request.PayloadJson,
                PreviousHash = previousHash
            };
            var hash = ComputeHash(envelope);
            envelope = envelope with { Hash = hash };

            await File.AppendAllTextAsync(
                path,
                JsonSerializer.Serialize(envelope, SerializerOptions) + "\n",
                cancellationToken).ConfigureAwait(false);
            return envelope;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SessionEvent>> ReadAsync(
        GuyabanoSessionId sessionId,
        long afterSequence = 0,
        CancellationToken cancellationToken = default)
    {
        var path = EventPath(sessionId);
        if (!File.Exists(path))
            return [];

        var events = await ReadAllAsync(path, cancellationToken).ConfigureAwait(false);
        return events.Where(item => item.Sequence > afterSequence).ToArray();
    }

    public async Task<SessionEvent?> VerifyChainAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var path = EventPath(sessionId);
        if (!File.Exists(path))
            return null;

        var events = await ReadAllAsync(path, cancellationToken).ConfigureAwait(false);
        string? previousHash = null;
        SessionEvent? last = null;
        foreach (var item in events)
        {
            if (!string.Equals(item.PreviousHash, previousHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Session event chain broken at sequence {item.Sequence}: previous hash mismatch.");
            var expected = ComputeHash(item);
            if (!string.Equals(item.Hash, expected, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Session event chain broken at sequence {item.Sequence}: event hash mismatch.");
            previousHash = item.Hash;
            last = item;
        }

        return last;
    }

    public void Dispose()
    {
        foreach (var gate in gates.Values)
            gate.Dispose();
        gatesGate.Dispose();
    }

    private async Task<IReadOnlyList<SessionEvent>> ReadAllAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var result = new List<SessionEvent>();
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var item = JsonSerializer.Deserialize<SessionEvent>(line, SerializerOptions);
            if (item is not null)
                result.Add(item);
        }

        return result;
    }

    private string EventPath(GuyabanoSessionId sessionId) =>
        Path.Combine(rootPath, sessionId.ToString(), "events.jsonl");

    private async Task<SemaphoreSlim> GetGateAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken)
    {
        await gatesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = sessionId.ToString();
            if (!gates.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                gates[key] = gate;
            }

            return gate;
        }
        finally
        {
            gatesGate.Release();
        }
    }

    private static string ComputeHash(SessionEvent envelope)
    {
        var canonical = envelope.IdempotencyKey is null
            ? JsonSerializer.Serialize(new
            {
                envelope.Sequence,
                envelope.EventId,
                sessionId = envelope.SessionId.ToString(),
                envelope.Actor,
                envelope.EventType,
                envelope.OccurredAt,
                envelope.CausationId,
                envelope.CorrelationId,
                envelope.CrossSystemRefs,
                envelope.PayloadJson,
                envelope.PreviousHash
            }, SerializerOptions)
            : JsonSerializer.Serialize(new
        {
            envelope.Sequence,
            envelope.EventId,
            sessionId = envelope.SessionId.ToString(),
            envelope.Actor,
            envelope.EventType,
            envelope.OccurredAt,
            envelope.CausationId,
            envelope.CorrelationId,
            envelope.IdempotencyKey,
            envelope.CrossSystemRefs,
            envelope.PayloadJson,
            envelope.PreviousHash
        }, SerializerOptions);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static bool EquivalentRequest(
        SessionEvent existing,
        SessionEventRequest replay) =>
        existing.SessionId == replay.SessionId &&
        existing.Actor == replay.Actor &&
        existing.EventType == replay.EventType &&
        existing.CausationId == replay.CausationId &&
        existing.CorrelationId == replay.CorrelationId &&
        EquivalentReferences(existing.CrossSystemRefs, replay.CrossSystemRefs) &&
        existing.PayloadJson == replay.PayloadJson;

    private static bool EquivalentReferences(
        IReadOnlyDictionary<string, string>? existing,
        IReadOnlyDictionary<string, string>? replay)
    {
        if (existing is null || replay is null)
            return existing is null && replay is null;
        return existing.Count == replay.Count && existing.All(item =>
            replay.TryGetValue(item.Key, out var value) && value == item.Value);
    }
}
