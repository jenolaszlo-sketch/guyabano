using Penghou.Zhinu;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WebTerminal.Services;

internal sealed class CodeGenerationWorkflowClient(
    WorkflowEngine workflowEngine)
    : ICodeGenerationWorkflowClient
{
    public async Task<string> StartAsync(
        string prompt,
        string? resumeFromWorkflowId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        CodeGenerationWorkflowResult? resumeFallback = null;
        var continuationMode = CodeGenerationContinuationMode.None;
        if (!string.IsNullOrWhiteSpace(resumeFromWorkflowId))
        {
            resumeFallback = await WaitForResultAsync(
                resumeFromWorkflowId,
                cancellationToken);
            continuationMode =
                CodeGenerationContinuationMode.BuildAndRepair;
        }

        var workflowId = Guid.NewGuid();
        await workflowEngine.StartAsync(
            CodeGenerationWorkflowConstants.WorkflowName,
            CodeGenerationWorkflowConstants.WorkflowVersion,
            new CodeGenerationWorkflowRequest(
                prompt,
                resumeFromWorkflowId,
                continuationMode,
                resumeFallback),
            workflowId,
            cancellationToken: cancellationToken);

        return workflowId.ToString("D");
    }

    public Task<CodeGenerationWorkflowResult> WaitForResultAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);

        if (!Guid.TryParse(workflowId, out var runId))
        {
            throw new ArgumentException(
                "Zhinu workflow IDs must be GUID values.",
                nameof(workflowId));
        }

        return workflowEngine.WaitForCompletionAsync<
            CodeGenerationWorkflowResult>(
            runId,
            cancellationToken: cancellationToken);
    }
}
