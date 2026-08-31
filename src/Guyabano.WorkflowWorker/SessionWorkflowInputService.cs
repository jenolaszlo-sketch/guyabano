using System.Text.Json;
using Guyabano.Session;
using Penghou.Zhinu;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Delivers a user response to one durable Zhinu signal wait and correlates its
/// authoritative send receipt into the immutable session ledger.
/// </summary>
public sealed class SessionWorkflowInputService(
    ISessionWorkflowRuntimeProvider runtimes,
    IGuyabanoSessionStore sessions,
    ISessionEventStore sessionEvents,
    ISessionDecisionLeaseProvider decisionLeases)
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<SessionInputResponseReceipt> ProvideAsync(
        Guid workflowRunId,
        Guid requestEventId,
        Guid responseId,
        string signalName,
        string actor,
        object? response,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("A workflow run ID is required.", nameof(workflowRunId));
        if (requestEventId == Guid.Empty)
            throw new ArgumentException("An input request event ID is required.", nameof(requestEventId));
        if (responseId == Guid.Empty)
            throw new ArgumentException("A response ID is required.", nameof(responseId));
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var session = await sessions.FindByWorkflowRunAsync(
                workflowRunId,
                cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' is not associated with a Guyabano session.");
        await using var decisionLease = await decisionLeases.AcquireAsync(
            session.Id,
            responseId,
            cancellationToken).ConfigureAwait(false);
        var history = await sessionEvents.ReadAsync(session.Id, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var request = history.SingleOrDefault(item => item.EventId == requestEventId) ??
            throw new KeyNotFoundException(
                $"Input request event '{requestEventId:D}' does not exist in session '{session.Id}'.");
        if (request.EventType != SessionEventTypes.InputRequested ||
            request.CorrelationId != workflowRunId)
        {
            throw new InvalidOperationException(
                $"Event '{requestEventId:D}' is not an input request for workflow '{workflowRunId:D}'.");
        }
        var requestedSignal = request.CrossSystemRefs?.GetValueOrDefault("signalName");
        if (!string.Equals(requestedSignal, signalName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Input request '{requestEventId:D}' expects signal '{requestedSignal ?? "(missing)"}', not '{signalName}'.");
        }
        var priorResponse = history.FirstOrDefault(item =>
            item.EventType == SessionEventTypes.InputProvided &&
            item.CausationId == requestEventId);

        await using var runtime = await runtimes.AcquireAsync(
            session.Id,
            cancellationToken).ConfigureAwait(false);
        // The request event is the stable identity of the one logical response
        // accepted by this wait. This prevents a crash followed by a newly
        // generated browser response ID from buffering a second signal.
        var signalReceipt = await runtime.Engine.SendSignalWithReceiptAsync(
            workflowRunId,
            signalName,
            new SignalSendOptions { SignalId = requestEventId },
            response,
            cancellationToken).ConfigureAwait(false);
        if (priorResponse is not null)
        {
            var priorResponseId = priorResponse.CrossSystemRefs?.GetValueOrDefault("responseId");
            if (!string.Equals(priorResponseId, responseId.ToString("D"), StringComparison.Ordinal))
            {
                throw new SessionInputAlreadyProvidedException(
                    $"Input request '{requestEventId:D}' was already answered by response '{priorResponseId ?? "unknown"}'.");
            }
            return CreateReceiptFromSessionEvent(
                session.Id,
                workflowRunId,
                requestEventId,
                responseId,
                signalName,
                priorResponse);
        }
        var references = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workflowRunId"] = workflowRunId.ToString("D"),
            ["requestEventId"] = requestEventId.ToString("D"),
            ["responseId"] = responseId.ToString("D"),
            ["signalId"] = signalReceipt.SignalId.ToString("D"),
            ["signalName"] = signalName,
            ["zhinuEventSequence"] = signalReceipt.Event.Sequence.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        };
        var sessionEvent = await sessionEvents.AppendAsync(
            new SessionEventRequest(
                session.Id,
                actor,
                SessionEventTypes.InputProvided,
                signalReceipt.Event.Timestamp,
                CausationId: requestEventId,
                CorrelationId: workflowRunId,
                CrossSystemRefs: references,
                PayloadJson: response is null
                    ? null
                    : JsonSerializer.Serialize(response, SerializerOptions),
                IdempotencyKey: $"input-response:{responseId:D}",
                PayloadSensitivity: SessionPayloadSensitivity.Confidential,
                PayloadRetention: SessionPayloadRetention.Retain),
            cancellationToken).ConfigureAwait(false);
        return new SessionInputResponseReceipt(
            session.Id,
            workflowRunId,
            requestEventId,
            responseId,
            signalName,
            signalReceipt.SignalId,
            signalReceipt.Event.Sequence,
            sessionEvent.EventId,
            signalReceipt.WasBuffered);
    }

    private static SessionInputResponseReceipt CreateReceiptFromSessionEvent(
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        Guid requestEventId,
        Guid responseId,
        string signalName,
        SessionEvent sessionEvent) => new(
            sessionId,
            workflowRunId,
            requestEventId,
            responseId,
            signalName,
            Guid.Parse(sessionEvent.CrossSystemRefs!["signalId"]),
            long.Parse(
                sessionEvent.CrossSystemRefs["zhinuEventSequence"],
                System.Globalization.CultureInfo.InvariantCulture),
            sessionEvent.EventId,
            WasBuffered: false);
}
