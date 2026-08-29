using System.Text.Json;
using Guyabano.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Penghou.Zhinu;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Mirrors authoritative Zhinu events into the immutable session ledger. The
/// append happens before the cursor advance, giving at-least-once delivery with
/// deterministic Siming idempotency after any crash boundary.
/// </summary>
public sealed class SessionWorkflowEventMirrorService(
    IGuyabanoSessionStore sessions,
    ISessionWorkflowRuntimeProvider runtimes,
    ISessionWorkflowEventMirrorStore cursors,
    ISessionEventStore sessionEvents,
    TimeProvider timeProvider,
    ILogger<SessionWorkflowEventMirrorService> logger,
    SessionWorkflowFailureRecoveryService? failureRecovery = null) : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<int> MirrorPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var mirrored = 0;
        foreach (var session in await sessions.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var runtime = await runtimes.AcquireAsync(
                session.Id, cancellationToken).ConfigureAwait(false);
            foreach (var workflowRunId in session.WorkflowRunIds)
            {
                var cursor = await cursors.GetAsync(
                    session.Id, workflowRunId, cancellationToken).ConfigureAwait(false);
                var sequence = cursor?.MirroredSequence ?? 0;
                while (true)
                {
                    var page = await runtime.Engine.GetEventsAsync(
                        workflowRunId,
                        sequence,
                        100,
                        cancellationToken).ConfigureAwait(false);
                    if (page.Count == 0)
                        break;
                    foreach (var workflowEvent in page)
                    {
                        if (workflowEvent.Sequence != sequence + 1)
                            throw new InvalidOperationException(
                                $"Zhinu event gap for workflow '{workflowRunId:D}': " +
                                $"expected {sequence + 1}, received {workflowEvent.Sequence}.");
                        var mirroredEvent = await sessionEvents.AppendAsync(
                            new SessionEventRequest(
                                session.Id,
                                Actor: "zhinu",
                                EventType: SessionEventTypes.ZhinuEventMirrored,
                                OccurredAt: workflowEvent.Timestamp,
                                CorrelationId: workflowRunId,
                                CrossSystemRefs: new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["workflowRunId"] = workflowRunId.ToString("D"),
                                    ["zhinuEventSequence"] = workflowEvent.Sequence.ToString(
                                        System.Globalization.CultureInfo.InvariantCulture),
                                    ["zhinuEventType"] = workflowEvent.EventType,
                                    ["stepKey"] = workflowEvent.StepKey ?? "(workflow)",
                                    ["attempt"] = workflowEvent.Attempt?.ToString(
                                        System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"
                                },
                                PayloadJson: JsonSerializer.Serialize(workflowEvent, SerializerOptions),
                                IdempotencyKey:
                                    $"zhinu-mirror:{workflowRunId:D}:{workflowEvent.Sequence}"),
                            cancellationToken).ConfigureAwait(false);
                        if (failureRecovery is not null)
                        {
                            await failureRecovery.RecordAsync(
                                session.Id,
                                workflowEvent,
                                mirroredEvent.EventId,
                                cancellationToken).ConfigureAwait(false);
                        }
                        await cursors.AdvanceAsync(
                            session.Id,
                            workflowRunId,
                            sequence,
                            workflowEvent.Sequence,
                            cancellationToken).ConfigureAwait(false);
                        sequence = workflowEvent.Sequence;
                        mirrored++;
                    }
                }
            }
        }
        return mirrored;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var mirrored = await MirrorPendingAsync(stoppingToken).ConfigureAwait(false);
                if (mirrored > 0)
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
                    "Zhinu-to-Siming event mirroring failed; durable cursors will resume delivery.");
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
