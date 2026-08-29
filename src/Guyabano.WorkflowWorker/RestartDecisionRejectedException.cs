namespace Guyabano.WorkflowWorker;

public sealed class RestartDecisionRejectedException(
    string reasonCode,
    string message) : InvalidOperationException(message)
{
    public string ReasonCode { get; } = reasonCode;
}
