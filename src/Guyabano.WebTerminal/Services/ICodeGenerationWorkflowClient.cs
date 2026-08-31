using Guyabano.CodeGeneration.Workflows;
using Guyabano.Session;
using Guyabano.WorkflowWorker;

namespace Guyabano.WebTerminal.Services;

public interface ICodeGenerationWorkflowClient
{
    Task<string> StartAsync(
        string prompt,
        string? resumeFromWorkflowId = null,
        CancellationToken cancellationToken = default);

    Task<CodeGenerationWorkflowResult> WaitForResultAsync(
        string workflowId,
        CancellationToken cancellationToken = default);

    Task<SessionInputResponseReceipt> ProvideInputAsync(
        string workflowId,
        string requestEventId,
        Guid responseId,
        string signalName,
        object? response,
        CancellationToken cancellationToken = default);

    Task<RestartPreview> PreviewFailedDecompositionRestartAsync(
        string workflowId,
        CancellationToken cancellationToken = default);

    Task<RestartOutcome> ApproveRestartAsync(
        RestartPreview preview,
        CancellationToken cancellationToken = default);
}
