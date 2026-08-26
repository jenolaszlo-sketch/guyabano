using Guyabano.CodeGeneration.Workflows;

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
}
