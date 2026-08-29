using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Guyabano.Session;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Delivers transactional catalog outbox receipts to the immutable session
/// ledger. Failures never roll back catalog state and are retried in session
/// order; one broken session does not block unrelated sessions.
/// </summary>
public sealed class SessionLifecycleReceiptDeliveryService(
    ISessionLifecycleReceiptStore receiptStore,
    ISessionEventStore sessionEvents,
    TimeProvider timeProvider,
    ILogger<SessionLifecycleReceiptDeliveryService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<int> DeliverPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var pending = await receiptStore.ListPendingAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var blockedSessions = new HashSet<GuyabanoSessionId>();
        var delivered = 0;
        foreach (var receipt in pending)
        {
            if (blockedSessions.Contains(receipt.SessionId))
                continue;
            try
            {
                var sessionEvent = await sessionEvents.AppendAsync(
                    new SessionEventRequest(
                        receipt.SessionId,
                        Actor: "guyabano",
                        EventType: receipt.EventType,
                        OccurredAt: receipt.OccurredAt,
                        CorrelationId: receipt.CorrelationId,
                        CrossSystemRefs: receipt.CrossSystemRefs,
                        IdempotencyKey: $"catalog-outbox:{receipt.IdempotencyKey}"),
                    cancellationToken).ConfigureAwait(false);
                await receiptStore.MarkDeliveredAsync(
                    receipt.ReceiptId,
                    sessionEvent.CommittedAt,
                    cancellationToken).ConfigureAwait(false);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                blockedSessions.Add(receipt.SessionId);
                logger.LogWarning(
                    exception,
                    "Session lifecycle receipt {ReceiptId} for {SessionId} remains pending and will be retried.",
                    receipt.ReceiptId,
                    receipt.SessionId);
            }
        }
        return delivered;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delivered = await DeliverPendingAsync(stoppingToken).ConfigureAwait(false);
                if (delivered > 0)
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Session lifecycle receipt scan failed; pending receipts will be retried.");
            }
            try
            {
                await Task.Delay(PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
