using Penghou.Zhinu;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Options;
using Guyabano.Session;
using System.Security.Cryptography;
using System.Text;

namespace Guyabano.WebTerminal.Services;

internal sealed class CodeGenerationWorkflowClient(
    ISessionWorkflowRuntimeProvider workflowRuntimes,
    IOptions<CodeGenerationWorkerOptions> options,
    CodeGenerationWorkspaceResolver workspaceResolver,
    IGuyabanoSessionStore sessionStore,
    ISessionEventStore sessionEvents,
    SessionWorkflowInputService inputService,
    CodeGenerationWorkflowRestartService restartService,
    IAuthenticatedActorProvider actorProvider)
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
        var workspace = workspaceResolver.EnsureAvailable(session);
        await sessionStore.AttachWorkflowRunAsync(
            session.Id,
            workflowId,
            cancellationToken);
        var request = new CodeGenerationWorkflowRequest(
            prompt,
            session.Id,
            resumeFromWorkflowId,
            continuationMode,
            resumeFallback)
        {
            GenerationModelTierCount =
                1 + settings.EscalationModels.Count
        };
        if (settings.RepositoryContextEnabled)
        {
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

    public Task<SessionInputResponseReceipt> ProvideInputAsync(
        string workflowId,
        string requestEventId,
        Guid responseId,
        string signalName,
        object? response,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(workflowId, out var workflowRunId))
            throw new ArgumentException("Zhinu workflow IDs must be GUID values.", nameof(workflowId));
        if (!Guid.TryParse(requestEventId, out var parsedRequestEventId))
            throw new ArgumentException("Input request event IDs must be GUID values.", nameof(requestEventId));
        var actor = actorProvider.GetRequiredActor();
        return inputService.ProvideAsync(
            workflowRunId,
            parsedRequestEventId,
            responseId,
            signalName,
            actor.SubjectId,
            response,
            cancellationToken);
    }

    public async Task<RestartPreview> PreviewFailedDecompositionRestartAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(workflowId, out var workflowRunId))
            throw new ArgumentException(
                "Zhinu workflow IDs must be GUID values.",
                nameof(workflowId));
        var result = await WaitForResultAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        var failed = result.Decompositions.LastOrDefault(item =>
            !item.Succeeded) ?? throw new InvalidOperationException(
                "The workflow result has no failed decomposition to retry.");
        var targetStepKey =
            $"decomposition/{result.ArchitectureVersion}/{failed.ParentTaskId}";
        return await restartService.PreviewAsync(
            workflowRunId,
            targetStepKey,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<RestartOutcome> ApproveRestartAsync(
        RestartPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var actor = actorProvider.GetRequiredActor();
        var approvalId = Guid.CreateVersion7();
        var changeSet = string.Join("\n",
            preview.WorkflowRunId.ToString("D"),
            preview.TargetStepKey,
            preview.WorkspaceRevision ?? string.Empty,
            string.Join("\n", preview.InvalidatedStepKeys
                .Order(StringComparer.Ordinal)));
        var changeSetHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(changeSet)))
            .ToLowerInvariant();
        return restartService.RestartAsync(new RestartApproval(
            approvalId,
            Guid.CreateVersion7(),
            preview.PreviewId,
            preview.WorkflowRunId,
            preview.TargetStepKey,
            preview.WorkspaceRevision,
            ApprovedIndexIdentity: null,
            changeSetHash,
            actor.SubjectId,
            Approved: true,
            ApprovedAt: DateTimeOffset.UtcNow),
            cancellationToken);
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
