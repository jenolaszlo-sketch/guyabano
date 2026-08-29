using Penghou.Zhinu;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Options;
using Guyabano.Session;

namespace Guyabano.WebTerminal.Services;

internal sealed class CodeGenerationWorkflowClient(
    ISessionWorkflowRuntimeProvider workflowRuntimes,
    IOptions<CodeGenerationWorkerOptions> options,
    CodeGenerationWorkspaceResolver workspaceResolver,
    IGuyabanoSessionStore sessionStore,
    ISessionEventStore sessionEvents)
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

        await using var workflowRuntime = await workflowRuntimes
            .AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false);
        await workflowRuntime.Engine.StartAsync(
            CodeGenerationWorkflowConstants.WorkflowName,
            CodeGenerationWorkflowConstants.WorkflowVersion,
            request,
            workflowId,
            cancellationToken: cancellationToken);

        var workflowRefs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionId"] = session.Id.ToString(),
            ["workflowRunId"] = workflowId.ToString("D"),
            ["continuationMode"] = continuationMode.ToString()
        };
        await sessionEvents.AppendAsync(new SessionEventRequest(
            session.Id,
            Actor: "user",
            EventType: SessionEventTypes.UserMessage,
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: workflowId,
            CrossSystemRefs: workflowRefs,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(new { prompt }),
            PayloadSensitivity: SessionPayloadSensitivity.Confidential,
            PayloadRetention: SessionPayloadRetention.Retain)).ConfigureAwait(false);
        await sessionEvents.AppendAsync(new SessionEventRequest(
            session.Id,
            Actor: "guyabano",
            EventType: SessionEventTypes.WorkflowStarted,
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: workflowId,
            CrossSystemRefs: workflowRefs)).ConfigureAwait(false);

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

        return WaitForSessionResultAsync(runId, cancellationToken);
    }

    private async Task<CodeGenerationWorkflowResult> WaitForSessionResultAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        var session = await sessionStore.FindByWorkflowRunAsync(
                workflowRunId,
                cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' is not associated with a Guyabano session.");
        await using var runtime = await workflowRuntimes
            .AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false);
        return await runtime.Engine.WaitForCompletionAsync<CodeGenerationWorkflowResult>(
            workflowRunId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
