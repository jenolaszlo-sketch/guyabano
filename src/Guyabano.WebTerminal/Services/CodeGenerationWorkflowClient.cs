using Penghou.Zhinu;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Options;

namespace Guyabano.WebTerminal.Services;

internal sealed class CodeGenerationWorkflowClient(
    WorkflowEngine workflowEngine,
    IOptions<CodeGenerationWorkerOptions> options)
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
        var settings = options.Value;
        var request = new CodeGenerationWorkflowRequest(
            prompt,
            resumeFromWorkflowId,
            continuationMode,
            resumeFallback);
        if (settings.RepositoryContextEnabled)
        {
            request = request with
            {
                Repository = new RepositoryReference(
                    settings.RepositoryId,
                    Path.GetFullPath(settings.OutputRoot),
                    settings.RepositorySymbolSeeds)
            };
        }

        await workflowEngine.StartAsync(
            CodeGenerationWorkflowConstants.WorkflowName,
            CodeGenerationWorkflowConstants.WorkflowVersion,
            request,
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
