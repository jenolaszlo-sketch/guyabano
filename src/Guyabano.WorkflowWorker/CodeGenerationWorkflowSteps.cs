using System.Collections.Concurrent;
using Penghou.Zhinu;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowWorker;

internal abstract class CodeGenerationWorkflowStep<TInput, TOutput>(
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    WorkflowStep<TInput, TOutput>
{
    public sealed override async Task<TOutput> ExecuteAsync(
        WorkflowStepContext context,
        TInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var heartbeatState = heartbeatStore.GetOrCreate(context);
        var runId = context.WorkflowRunId.ToString("D");
        using var zhinuScope = CodeGenerationZhinuStepScope.Push(context);
        using var scope = CodeGenerationActivityExecutionContext.Push(
            new CodeGenerationActivityExecutionContext(
                runId,
                runId,
                context.StepKey,
                context.Attempt,
                cancellationToken,
                heartbeatState));
        try
        {
            var result = await ExecuteCoreAsync(input, cancellationToken)
                .ConfigureAwait(false);
            heartbeatStore.Remove(context);
            return result;
        }
        catch (OperationCanceledException)
        {
            heartbeatStore.Remove(context);
            throw;
        }
        catch (CodeGenerationActivityException exception)
            when (exception.NonRetryable)
        {
            heartbeatStore.Remove(context);
            throw;
        }
    }

    protected abstract Task<TOutput> ExecuteCoreAsync(
        TInput input,
        CancellationToken cancellationToken);
}

internal sealed class PlanCodeGenerationStep(
    CodeGenerationPlanningActivities activities,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        CodeGenerationWorkflowRequest,
        CodeGenerationWorkflowResult>(heartbeatStore)
{
    protected override Task<CodeGenerationWorkflowResult> ExecuteCoreAsync(
        CodeGenerationWorkflowRequest input,
        CancellationToken cancellationToken) =>
        activities.PlanAsync(input);
}

internal sealed class IndexRepositoryStep(
    IRepositoryContextService repositoryContext,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<RepositoryIndexRequest, RepositoryRevision>(
        heartbeatStore)
{
    protected override Task<RepositoryRevision> ExecuteCoreAsync(
        RepositoryIndexRequest input,
        CancellationToken cancellationToken) =>
        repositoryContext.IndexAsync(input, cancellationToken);
}

internal sealed class SelectRepositoryContextStep(
    IRepositoryContextService repositoryContext,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        RepositoryContextSelectionRequest,
        RepositoryContextSelection>(heartbeatStore)
{
    protected override Task<RepositoryContextSelection> ExecuteCoreAsync(
        RepositoryContextSelectionRequest input,
        CancellationToken cancellationToken) =>
        repositoryContext.SelectAsync(input, cancellationToken);
}

internal sealed class CaptureRepositoryContextStep(
    IRepositoryContextService repositoryContext,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        RepositoryContextCaptureRequest,
        RepositoryContextReference>(heartbeatStore)
{
    protected override Task<RepositoryContextReference> ExecuteCoreAsync(
        RepositoryContextCaptureRequest input,
        CancellationToken cancellationToken) =>
        repositoryContext.CaptureAsync(input, cancellationToken);
}

internal sealed class DecomposeCodeGenerationTaskStep(
    CodeGenerationDecompositionActivities activities,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        CodeGenerationDecompositionWorkflowRequest,
        CodeGenerationDecompositionWorkflowResult>(heartbeatStore)
{
    protected override Task<CodeGenerationDecompositionWorkflowResult>
        ExecuteCoreAsync(
            CodeGenerationDecompositionWorkflowRequest input,
            CancellationToken cancellationToken) =>
        activities.DecomposeAsync(input);
}

internal sealed class ReviewCodeGenerationArchitectureStep(
    CodeGenerationArchitectureActivities activities,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        ArchitectureReviewWorkflowRequest,
        ArchitectureReviewWorkflowResult>(heartbeatStore)
{
    protected override Task<ArchitectureReviewWorkflowResult> ExecuteCoreAsync(
        ArchitectureReviewWorkflowRequest input,
        CancellationToken cancellationToken) =>
        activities.ReviewAsync(input);
}

internal sealed class ResolveCodeGenerationArchitectureGapStep(
    CodeGenerationArchitectureActivities activities,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        ArchitectureGapResolutionWorkflowRequest,
        ArchitectureGapResolutionWorkflowResult>(heartbeatStore)
{
    protected override Task<ArchitectureGapResolutionWorkflowResult>
        ExecuteCoreAsync(
            ArchitectureGapResolutionWorkflowRequest input,
            CancellationToken cancellationToken) =>
        activities.ResolveGapAsync(input);
}

internal sealed class IntegrateCodeGenerationArchitectureStep(
    CodeGenerationArchitectureActivities activities,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        ArchitectureDecisionIntegrationWorkflowRequest,
        ArchitectureDecisionIntegrationWorkflowResult>(heartbeatStore)
{
    protected override Task<ArchitectureDecisionIntegrationWorkflowResult>
        ExecuteCoreAsync(
            ArchitectureDecisionIntegrationWorkflowRequest input,
            CancellationToken cancellationToken) =>
        activities.IntegrateAsync(input);
}

internal sealed class ScaffoldCodeGenerationStep(
    CodeGenerationScaffoldingActivities activities,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        CodeGenerationScaffoldingRequest,
        CodeGenerationScaffoldingResult>(heartbeatStore)
{
    protected override Task<CodeGenerationScaffoldingResult> ExecuteCoreAsync(
        CodeGenerationScaffoldingRequest input,
        CancellationToken cancellationToken) =>
        activities.ScaffoldAsync(input);
}

internal sealed class GenerateCodeTaskStep(
    CodeGenerationTaskActivities activities,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        CodeGenerationTaskWorkflowRequest,
        CodeGenerationTaskWorkflowResult>(heartbeatStore)
{
    protected override Task<CodeGenerationTaskWorkflowResult> ExecuteCoreAsync(
        CodeGenerationTaskWorkflowRequest input,
        CancellationToken cancellationToken) =>
        activities.GenerateAsync(input);
}

internal sealed class BuildGeneratedCodeStep(
    CodeGenerationBuildActivities activities,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        CodeGenerationBuildRequest,
        CodeGenerationBuildResult>(heartbeatStore)
{
    protected override Task<CodeGenerationBuildResult> ExecuteCoreAsync(
        CodeGenerationBuildRequest input,
        CancellationToken cancellationToken) =>
        activities.BuildAsync(input);
}

internal sealed class LoadCodeGenerationCheckpointStep(
    CodeGenerationCheckpointActivities activities,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        CodeGenerationCheckpointLoadRequest,
        CodeGenerationRunCheckpoint>(heartbeatStore)
{
    protected override Task<CodeGenerationRunCheckpoint> ExecuteCoreAsync(
        CodeGenerationCheckpointLoadRequest input,
        CancellationToken cancellationToken) =>
        activities.LoadAsync(input);
}

internal sealed class SaveCodeGenerationCheckpointStep(
    CodeGenerationCheckpointActivities activities,
    CodeGenerationActivityHeartbeatStore heartbeatStore) :
    CodeGenerationWorkflowStep<
        CodeGenerationCheckpointRequest,
        Guyabano.Artifacts.ArtifactReference>(heartbeatStore)
{
    protected override Task<Guyabano.Artifacts.ArtifactReference>
        ExecuteCoreAsync(
            CodeGenerationCheckpointRequest input,
            CancellationToken cancellationToken) =>
        activities.SaveAsync(input);
}

internal sealed class CodeGenerationActivityHeartbeatStore(
    TimeProvider timeProvider)
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<HeartbeatKey, HeartbeatEntry> entries =
        new();
    private int accessCount;

    public CodeGenerationActivityHeartbeatState GetOrCreate(
        WorkflowStepContext context)
    {
        PrunePeriodically();
        var now = timeProvider.GetUtcNow();
        var entry = entries.GetOrAdd(
            HeartbeatKey.From(context),
            _ => new HeartbeatEntry(
                new CodeGenerationActivityHeartbeatState(),
                now.UtcTicks));
        entry.Touch(now.UtcTicks);
        return entry.State;
    }

    public void Remove(WorkflowStepContext context) =>
        entries.TryRemove(HeartbeatKey.From(context), out _);

    private void PrunePeriodically()
    {
        if (Interlocked.Increment(ref accessCount) % 64 != 0)
            return;

        var cutoffTicks = (timeProvider.GetUtcNow() - Retention).UtcTicks;
        foreach (var entry in entries)
        {
            if (entry.Value.LastAccessTicks < cutoffTicks)
                entries.TryRemove(entry.Key, out _);
        }
    }

    private sealed record HeartbeatKey(
        Guid WorkflowRunId,
        string StepKey,
        int Revision)
    {
        public static HeartbeatKey From(WorkflowStepContext context) =>
            new(context.WorkflowRunId, context.StepKey, context.Revision);
    }

    private sealed class HeartbeatEntry(
        CodeGenerationActivityHeartbeatState state,
        long lastAccessTicks)
    {
        private long lastAccessTicks = lastAccessTicks;

        public CodeGenerationActivityHeartbeatState State { get; } = state;

        public long LastAccessTicks => Volatile.Read(ref lastAccessTicks);

        public void Touch(long value) =>
            Volatile.Write(ref lastAccessTicks, value);
    }
}
