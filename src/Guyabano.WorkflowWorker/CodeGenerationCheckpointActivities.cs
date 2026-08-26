using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Messaging;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationCheckpointActivities(
    IArtifactRepository artifactRepository,
    IWorkflowProgressPublisher progressPublisher,
    ILogger<CodeGenerationCheckpointActivities> logger)
{
    private const string ArtifactKind = "workflow-checkpoint";
    private const string StageKey = "latest";

    public async Task<CodeGenerationRunCheckpoint> LoadAsync(
        CodeGenerationCheckpointLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = CodeGenerationActivityExecutionContext.Current;
        var workflowId = context.Info.WorkflowId ??
            throw new InvalidOperationException("Workflow ID is unavailable.");

        var envelope = await artifactRepository.ReadLatestAsync<
            CodeGenerationRunCheckpoint>(
            request.SourceWorkflowId,
            ArtifactKind,
            StageKey,
            context.CancellationToken);
        CodeGenerationRunCheckpoint checkpoint;
        if (envelope is not null)
        {
            checkpoint = envelope.Payload;
        }
        else if (request.FallbackResult is not null)
        {
            checkpoint = new CodeGenerationRunCheckpoint(
                request.SourceWorkflowId,
                request.Prompt,
                request.FallbackResult);
            await WriteAsync(checkpoint, context.CancellationToken);
            logger.LogInformation(
                "Migrated fallback result for workflow {SourceWorkflowId} into an artifact checkpoint.",
                request.SourceWorkflowId);
        }
        else
        {
            throw new InvalidOperationException(
                $"Workflow '{request.SourceWorkflowId}' has no artifact checkpoint and no migration fallback was supplied.");
        }

        if (!checkpoint.WorkflowId.Equals(
                request.SourceWorkflowId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The loaded checkpoint does not belong to the requested source workflow.");
        }

        var errors = CodeGenerationContinuationValidator
            .ValidateBuildAndRepair(checkpoint);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Workflow '{request.SourceWorkflowId}' cannot continue at BuildAndRepair: {string.Join(" ", errors)}");
        }

        await PublishSafelyAsync(workflowId, new WorkflowProgress(
            WorkflowProgressEventType.Completed,
            "Load artifact checkpoint",
            $"Loaded validated build-and-repair state from {request.SourceWorkflowId}.",
            DateTimeOffset.UtcNow,
            RunId: context.Info.WorkflowRunId,
            ActivityId: context.Info.ActivityId,
            Attempt: context.Info.Attempt,
            Succeeded: true,
            Metadata: new Dictionary<string, string>
            {
                ["sourceWorkflowId"] = request.SourceWorkflowId,
                ["continuationMode"] =
                    CodeGenerationContinuationMode.BuildAndRepair.ToString()
            }));
        return checkpoint;
    }

    public async Task<ArtifactReference> SaveAsync(
        CodeGenerationCheckpointRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checkpoint = new CodeGenerationRunCheckpoint(
            request.WorkflowId,
            request.Prompt,
            request.Result);
        var envelope = await WriteAsync(
            checkpoint,
            CodeGenerationActivityExecutionContext.Current.CancellationToken);
        return envelope.Reference;
    }

    private Task<ArtifactEnvelope<CodeGenerationRunCheckpoint>> WriteAsync(
        CodeGenerationRunCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        artifactRepository.WriteAsync(
            new ArtifactWriteRequest<CodeGenerationRunCheckpoint>(
                checkpoint.WorkflowId,
                ArtifactKind,
                1,
                StageKey,
                ArtifactStatus.Validated,
                checkpoint),
            cancellationToken);

    private async Task PublishSafelyAsync(
        string workflowId,
        WorkflowProgress progress)
    {
        try
        {
            await progressPublisher.PublishAsync(
                workflowId,
                progress,
                CodeGenerationActivityExecutionContext.Current.CancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to publish checkpoint progress for workflow {WorkflowId}.",
                workflowId);
        }
    }
}
