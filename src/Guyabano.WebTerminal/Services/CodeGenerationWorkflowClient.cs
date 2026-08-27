using Penghou.Zhinu;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Options;
using Guyabano.Session;

namespace Guyabano.WebTerminal.Services;

internal sealed class CodeGenerationWorkflowClient(
    WorkflowEngine workflowEngine,
    IOptions<CodeGenerationWorkerOptions> options,
    CodeGenerationWorkspaceResolver workspaceResolver,
    IGuyabanoSessionStore sessionStore)
    : ICodeGenerationWorkflowClient
{
    public async Task<string> StartAsync(
        string prompt,
        string? resumeFromWorkflowId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        CodeGenerationWorkflowResult? resumeFallback = null;
        GuyabanoSession? session = null;
        var continuationMode = CodeGenerationContinuationMode.None;
        if (!string.IsNullOrWhiteSpace(resumeFromWorkflowId))
        {
            resumeFallback = await WaitForResultAsync(
                resumeFromWorkflowId,
                cancellationToken);
            continuationMode =
                CodeGenerationContinuationMode.BuildAndRepair;
            if (!Guid.TryParse(resumeFromWorkflowId, out var sourceRunId))
                throw new ArgumentException(
                    "Zhinu workflow IDs must be GUID values.",
                    nameof(resumeFromWorkflowId));
            session = await sessionStore.FindByWorkflowRunAsync(
                sourceRunId,
                cancellationToken);
        }

        var workflowId = Guid.CreateVersion7();
        var settings = options.Value;
        if (session is null)
        {
            var sessionId = GuyabanoSessionId.New();
            session = await sessionStore.CreateAsync(
                settings.RepositoryId,
                $"workspace:{sessionId}",
                sessionId,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(resumeFromWorkflowId))
            {
                await sessionStore.AttachWorkflowRunAsync(
                    session.Id,
                    Guid.Parse(resumeFromWorkflowId),
                    cancellationToken);
            }
        }
        await sessionStore.AttachWorkflowRunAsync(
            session.Id,
            workflowId,
            cancellationToken);
        var request = new CodeGenerationWorkflowRequest(
            prompt,
            session.Id,
            resumeFromWorkflowId,
            continuationMode,
            resumeFallback);
        if (settings.RepositoryContextEnabled)
        {
            var workspace = workspaceResolver.Resolve(session.Id);
            request = request with
            {
                Repository = new RepositoryReference(
                    settings.RepositoryId,
                    workspace.HostPath,
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
