using Guyabano.Session;

namespace Guyabano.WorkflowWorker;

public sealed class RestartDecisionRejectedException(
    string reasonCode,
    string message) : InvalidOperationException(message)
{
    public string ReasonCode { get; } = reasonCode;

    public SessionRecoveryOutcome? RecoveryOutcome { get; private set; }

    public Guid? ReplacementPreviewId { get; private set; }

    internal void AttachRecovery(SessionRecoveryExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RecoveryOutcome = result.Outcome;
        ReplacementPreviewId = result.Receipt?.ResourceType == "impact-preview" &&
            Guid.TryParse(result.Receipt.ResourceId, out var previewId)
            ? previewId
            : null;
    }
}
