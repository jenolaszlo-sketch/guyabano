using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Guyabano.Session;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Replays authoritative session ledger entries into rebuildable projections.
/// Discovery compares each cataloged session's applied cursor with its ledger,
/// so repair does not depend on a lag marker having been written successfully.
/// </summary>
public sealed class SessionProjectionRepairService(
    IGuyabanoSessionStore sessionStore,
    ISessionEventStore sessionEvents,
    ISessionProjectionStore projections,
    ISessionProjectionDeliveryStore delivery,
    TimeProvider timeProvider,
    ILogger<SessionProjectionRepairService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<int> RepairPendingAsync(
        int maximumEventsPerSession = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumEventsPerSession is < 1 or > SessionEventPageRequest.MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumEventsPerSession));
        var repaired = 0;
        var sessions = await sessionStore.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projection = await projections.GetAsync(session.Id, cancellationToken)
                .ConfigureAwait(false);
            var page = await sessionEvents.ReadPageAsync(
                new SessionEventPageRequest(
                    session.Id,
                    projection?.AppliedSequence ?? 0,
                    maximumEventsPerSession),
                cancellationToken).ConfigureAwait(false);
            foreach (var sessionEvent in page.Events)
            {
                try
                {
                    await delivery.RecordCommittedAsync(sessionEvent, cancellationToken)
                        .ConfigureAwait(false);
                    await projections.ApplyAsync(sessionEvent, cancellationToken)
                        .ConfigureAwait(false);
                    repaired++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    try
                    {
                        await delivery.RecordFailureAsync(
                            sessionEvent,
                            exception,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception statusException)
                    {
                        logger.LogError(
                            statusException,
                            "Could not persist projection repair failure for session {SessionId} sequence {Sequence}.",
                            session.Id,
                            sessionEvent.Sequence);
                    }
                    logger.LogWarning(
                        exception,
                        "Projection repair for session {SessionId} stopped at sequence {Sequence}; it will retry.",
                        session.Id,
                        sessionEvent.Sequence);
                    break;
                }
            }
        }
        return repaired;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var repaired = await RepairPendingAsync(cancellationToken: stoppingToken)
                    .ConfigureAwait(false);
                if (repaired > 0)
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
                    "Session projection repair scan failed; the next scan will retry.");
            }
            try
            {
                await Task.Delay(PollInterval, timeProvider, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
