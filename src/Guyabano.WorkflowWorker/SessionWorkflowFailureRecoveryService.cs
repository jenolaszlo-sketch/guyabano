using System.Security.Cryptography;
using System.Text;
using Guyabano.Session;
using Penghou.Zhinu;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Converts committed terminal Zhinu failures into deterministic, forward-only
/// session incidents. The deterministic identities make crash replay safe.
/// </summary>
public sealed class SessionWorkflowFailureRecoveryService(
    SessionRecoveryCoordinator recovery)
{
    public async Task RecordAsync(
        GuyabanoSessionId sessionId,
        WorkflowEvent workflowEvent,
        Guid mirroredEventId,
        CancellationToken cancellationToken = default)
    {
        if (workflowEvent.EventType is not (
            WorkflowEventTypes.WorkflowFailed or
            WorkflowEventTypes.WorkflowCancelled or
            WorkflowEventTypes.CompensationFailed))
        {
            return;
        }

        var classification = Classify(workflowEvent);
        var incidentId = DeterministicId(
            $"incident\n{workflowEvent.WorkflowRunId:D}\n{workflowEvent.Sequence}\n{classification.ReasonCode}");
        var planId = DeterministicId($"plan\n{incidentId:D}\n{classification.Action}");
        var references = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workflowRunId"] = workflowEvent.WorkflowRunId.ToString("D"),
            ["zhinuEventSequence"] = workflowEvent.Sequence.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["zhinuEventType"] = workflowEvent.EventType,
            ["stepKey"] = workflowEvent.StepKey ?? "(workflow)",
            ["failureClass"] = classification.ReasonCode
        };
        var incident = new SessionIncident(
            incidentId,
            sessionId,
            classification.ReasonCode,
            classification.Severity,
            classification.Explanation,
            workflowEvent.Timestamp,
            workflowEvent.WorkflowRunId,
            references,
            mirroredEventId);
        var detected = await recovery.DetectAsync(incident, cancellationToken)
            .ConfigureAwait(false);
        var plan = new SessionRecoveryPlan(
            planId,
            incidentId,
            sessionId,
            classification.Action,
            classification.Explanation,
            SafeWorkspaceRevision: null,
            Automatic: false,
            PlannedAt: workflowEvent.Timestamp,
            workflowEvent.WorkflowRunId,
            references);
        var planned = await recovery.PlanAsync(plan, detected.EventId, cancellationToken)
            .ConfigureAwait(false);
        await recovery.CompleteAsync(
            new SessionRecoveryResolution(
                planId,
                incidentId,
                sessionId,
                SessionRecoveryOutcome.UserActionRequired,
                Attempt: 0,
                classification.UserAction,
                workflowEvent.Timestamp,
                workflowEvent.WorkflowRunId,
                references),
            planned.EventId,
            cancellationToken).ConfigureAwait(false);
    }

    private static FailureClassification Classify(WorkflowEvent workflowEvent)
    {
        var detail = workflowEvent.DataJson ?? string.Empty;
        if (workflowEvent.EventType == WorkflowEventTypes.WorkflowCancelled)
        {
            return new(
                "WorkflowCancelled",
                SessionIncidentSeverity.Warning,
                SessionRecoveryAction.HaltMutation,
                "The workflow was cancelled. Durable Zhinu state and the last accepted workspace remain authoritative.",
                "Review the cancellation reason, then explicitly resume or start a replacement operation.");
        }
        if (detail.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "WorkflowTimedOut",
                SessionIncidentSeverity.Error,
                SessionRecoveryAction.RetryIdempotently,
                "Workflow execution exhausted its timeout policy; the accepted workspace was not rolled back.",
                "Inspect the failed step receipt and retry the idempotent operation with an appropriate timeout.");
        }
        if (detail.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("LlmClient", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("HTTP", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "ProviderFailure",
                SessionIncidentSeverity.Error,
                SessionRecoveryAction.RetryIdempotently,
                "A model or external provider failure exhausted the durable Zhinu retry policy.",
                "Review the provider diagnostic and retry from the failed step; previously accepted state remains safe.");
        }
        if (detail.Contains("reindex", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("publication", StringComparison.OrdinalIgnoreCase) ||
            workflowEvent.StepKey?.Contains("reindex", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new(
                "DownstreamPublicationFailed",
                SessionIncidentSeverity.Error,
                SessionRecoveryAction.ReconcileForward,
                "A downstream graph or artifact publication failed after earlier durable work may have committed.",
                "Keep the accepted workspace and replay only missing publication participants from their receipts.");
        }
        return new(
            "WorkflowExecutionFailed",
            SessionIncidentSeverity.Error,
            SessionRecoveryAction.ReconcileForward,
            "Zhinu recorded a terminal workflow failure; no workflow or session history was rolled back.",
            "Inspect the failed Zhinu step and participant receipts, then reconcile forward from the accepted workspace.");
    }

    private static Guid DeterministicId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record FailureClassification(
        string ReasonCode,
        SessionIncidentSeverity Severity,
        SessionRecoveryAction Action,
        string Explanation,
        string UserAction);
}
