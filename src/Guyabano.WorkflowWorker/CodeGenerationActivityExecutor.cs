using Penghou.Zhinu;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationActivityExecutor(
    CodeGenerationPlanningActivities planning,
    CodeGenerationDecompositionActivities decomposition,
    CodeGenerationArchitectureActivities architecture,
    CodeGenerationScaffoldingActivities scaffolding,
    CodeGenerationTaskActivities tasks,
    CodeGenerationBuildActivities builds,
    CodeGenerationCheckpointActivities checkpoints)
    : ICodeGenerationActivityExecutor
{
    public Task<TOutput> ExecuteAsync<TOutput>(
        WorkflowContext workflow,
        string stepKey,
        string activityName,
        object input,
        StepOptions options,
        CancellationToken cancellationToken = default) =>
        ExecuteStepAsync<TOutput>(
            workflow,
            stepKey,
            activityName,
            input,
            options,
            cancellationToken);

    private Task<TOutput> ExecuteStepAsync<TOutput>(
        WorkflowContext workflow,
        string stepKey,
        string activityName,
        object input,
        StepOptions options,
        CancellationToken cancellationToken)
    {
        var heartbeatState = new CodeGenerationActivityHeartbeatState();
        return workflow.StepAsync(
            stepKey,
            input,
            async (value, step, token) =>
            {
                var runId = workflow.WorkflowRunId.ToString("D");
                using var scope =
                    CodeGenerationActivityExecutionContext.Push(
                        new CodeGenerationActivityExecutionContext(
                            runId,
                            runId,
                            stepKey,
                            step.Attempt,
                            token,
                            heartbeatState));
                var result = await InvokeAsync(
                    activityName,
                    value,
                    token);
                return result is TOutput typed
                    ? typed
                    : throw new InvalidOperationException(
                        $"Activity '{activityName}' returned '{result?.GetType().FullName ?? "null"}', not '{typeof(TOutput).FullName}'.");
            },
            options,
            cancellationToken);
    }

    private async Task<object?> InvokeAsync<TInput>(
        string activityName,
        TInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return (activityName, input) switch
        {
            (CodeGenerationWorkflowConstants.PlanActivity,
                CodeGenerationWorkflowRequest request) =>
                await planning.PlanAsync(request),
            (CodeGenerationWorkflowConstants.DecomposeTaskActivity,
                CodeGenerationDecompositionWorkflowRequest request) =>
                await decomposition.DecomposeAsync(request),
            (CodeGenerationWorkflowConstants.ReviewArchitectureActivity,
                ArchitectureReviewWorkflowRequest request) =>
                await architecture.ReviewAsync(request),
            (CodeGenerationWorkflowConstants.ResolveArchitectureGapActivity,
                ArchitectureGapResolutionWorkflowRequest request) =>
                await architecture.ResolveGapAsync(request),
            (CodeGenerationWorkflowConstants.IntegrateArchitectureDecisionsActivity,
                ArchitectureDecisionIntegrationWorkflowRequest request) =>
                await architecture.IntegrateAsync(request),
            (CodeGenerationWorkflowConstants.ScaffoldActivity,
                CodeGenerationScaffoldingRequest request) =>
                await scaffolding.ScaffoldAsync(request),
            (CodeGenerationWorkflowConstants.GenerateTaskActivity,
                CodeGenerationTaskWorkflowRequest request) =>
                await tasks.GenerateAsync(request),
            (CodeGenerationWorkflowConstants.BuildActivity,
                CodeGenerationBuildRequest request) =>
                await builds.BuildAsync(request),
            (CodeGenerationWorkflowConstants.LoadCheckpointActivity,
                CodeGenerationCheckpointLoadRequest request) =>
                await checkpoints.LoadAsync(request),
            (CodeGenerationWorkflowConstants.SaveCheckpointActivity,
                CodeGenerationCheckpointRequest request) =>
                await checkpoints.SaveAsync(request),
            _ => throw new InvalidOperationException(
                $"Unknown code-generation activity '{activityName}' for input '{typeof(TInput).FullName}'.")
        };
    }
}
