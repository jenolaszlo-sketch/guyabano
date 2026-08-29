namespace Guyabano.Session;

/// <summary>
/// Transactional outbox record produced by the operational catalog. Delivery
/// to Siming is retry-safe and does not roll back the catalog mutation.
/// </summary>
public sealed record SessionLifecycleReceipt
{
    public required Guid ReceiptId { get; init; }

    public required GuyabanoSessionId SessionId { get; init; }

    public required string EventType { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string IdempotencyKey { get; init; }

    public Guid? CorrelationId { get; init; }

    public IReadOnlyDictionary<string, string> CrossSystemRefs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public DateTimeOffset? DeliveredAt { get; init; }
}

public interface ISessionLifecycleReceiptStore
{
    Task<IReadOnlyList<SessionLifecycleReceipt>> ListPendingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default);

    Task MarkDeliveredAsync(
        Guid receiptId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default);
}
