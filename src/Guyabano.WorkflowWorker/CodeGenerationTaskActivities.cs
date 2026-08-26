using Microsoft.Extensions.Options;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.CodeGeneration;
using Guyabano.Llm.Prompting;
using Guyabano.Messaging;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationTaskActivities(
    ICodeGenerationTaskService taskService,
    IWorkflowProgressPublisher progressPublisher,
    IOptions<CodeGenerationWorkerOptions> options,
    ILogger<CodeGenerationTaskActivities> logger)
{
    public async Task<CodeGenerationTaskWorkflowResult> GenerateAsync(
        CodeGenerationTaskWorkflowRequest request)
    {
        var settings = options.Value;
        var context = CodeGenerationActivityExecutionContext.Current;
        var info = context.Info;
        var workflowId = info.WorkflowId ??
            throw new InvalidOperationException(
                "Workflow activity information did not include a workflow ID.");
        var task = request.Task;
        var attempt = info.Attempt;
        var maximumAttempts = CodeGenerationModelSelector.MaximumAttempts(
            settings,
            request.StartingModelTier);
        var selection = CodeGenerationModelSelector.Select(
            settings,
            attempt,
            request.StartingModelTier);
        var model = selection.Model;
        var previousAttempt =
            await ReadPreviousAttemptAsync(info) ??
            MapCorrection(request.Correction);
        var maxTokens = CodeGenerationTokenBudgetSelector.Select(
            settings,
            model,
            selection.ModelAttempt);
        var stage = request.IsBuildRepair
            ? $"Repair {task.Id}: {task.Title}"
            : $"{task.Id}: {task.Title}";

        await PublishSafelyAsync(workflowId, new WorkflowProgress(
            WorkflowProgressEventType.Started,
            stage,
            CreateStartedMessage(
                attempt,
                maximumAttempts,
                selection,
                previousAttempt),
            DateTimeOffset.UtcNow,
            RunId: info.WorkflowRunId,
            ActivityId: info.ActivityId,
            Attempt: attempt,
            Model: model,
            MaximumAttempts: maximumAttempts));

        try
        {
            var taskContext =
                await CodeGenerationTaskContextFactory.CreateAsync(
                    request.Plan,
                    request.ParentTaskId,
                    request.Task,
                    settings.OutputRoot,
                    context.CancellationToken);
            if (previousAttempt is not null)
            {
                taskContext = taskContext with
                {
                    Retry = previousAttempt
                };
            }

            var outcome = await taskService.GenerateAndEmitAsync(
                taskContext,
                settings.OutputRoot,
                model,
                maxTokens,
                context.CancellationToken);
            var actualModel = outcome.Diagnostics?.ActualModel ??
                outcome.Model;
            var willRetry = !outcome.Succeeded &&
                CodeGenerationRetryPolicy.ShouldRetry(
                    outcome,
                    attempt,
                    maximumAttempts);
            var metadata = new Dictionary<string, string>
            {
                ["taskId"] = task.Id,
                ["failure"] = outcome.Failure.ToString(),
                ["writtenFileCount"] = outcome.WrittenFiles.Count.ToString(),
                ["jsonWasRepaired"] = outcome.JsonWasRepaired.ToString(),
                ["maxTokens"] = maxTokens.ToString(),
                ["modelTier"] = selection.Tier.ToString(),
                ["modelAttempt"] = selection.ModelAttempt.ToString(),
                ["maximumModelAttempts"] =
                    CodeGenerationWorkflowConstants
                        .MaximumAttemptsPerModel
                        .ToString(),
                ["operation"] = request.IsBuildRepair
                    ? "build-repair"
                    : "generation",
                ["buildRepairCycle"] =
                    request.BuildRepairCycle.ToString()
            };

            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                outcome.Succeeded
                    ? WorkflowProgressEventType.Completed
                    : WorkflowProgressEventType.Failed,
                stage,
                outcome.Succeeded
                    ? $"Task completed with {outcome.WrittenFiles.Count} written file(s)."
                    : CreateFailureMessage(outcome.Error, willRetry),
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: attempt,
                Model: actualModel,
                GeneratedTokens: outcome.Usage?.CompletionTokens,
                Succeeded: outcome.Succeeded,
                Metadata: metadata,
                Diagnostics: CodeGenerationProgressDiagnostics.Create(outcome),
                MaximumAttempts: maximumAttempts,
                WillRetry: willRetry,
                FileChecks: CodeGenerationFileChecks.Create(outcome)));

            if (willRetry)
            {
                context.Heartbeat(
                    CodeGenerationRetryContextFactory.Create(
                        outcome,
                        attempt,
                        actualModel,
                        settings.OutputRoot));
                throw new CodeGenerationActivityException(
                    outcome.Error ?? $"Task '{task.Id}' failed.",
                    errorType: outcome.Failure.ToString(),
                    nonRetryable: false);
            }

            return Map(
                task.Id,
                outcome,
                settings.OutputRoot,
                selection.Tier,
                request.IsBuildRepair,
                request.BuildRepairCycle);
        }
        catch (CodeGenerationActivityException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                WorkflowProgressEventType.Canceled,
                stage,
                "Task generation was canceled.",
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: attempt,
                Model: model,
                Succeeded: false,
                MaximumAttempts: maximumAttempts));
            throw;
        }
        catch (Exception exception)
        {
            var transient = ActivityExceptionClassifier.IsTransient(exception);
            var willRetry = transient && attempt < maximumAttempts;
            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                WorkflowProgressEventType.Failed,
                stage,
                willRetry
                    ? $"{exception.Message} Task generation will retry automatically."
                    : exception.Message,
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: attempt,
                Model: model,
                Succeeded: false,
                Metadata: new Dictionary<string, string>
                {
                    ["taskId"] = task.Id,
                    ["exceptionType"] = exception.GetType().FullName ??
                        exception.GetType().Name,
                    ["failureKind"] = "UnhandledActivityException"
                },
                Diagnostics:
                [
                    new WorkflowDiagnostic(
                        WorkflowDiagnosticSeverity.Error,
                        "generation-activity-exception",
                        "Task generation failed unexpectedly.",
                        [exception.Message])
                ],
                MaximumAttempts: maximumAttempts,
                WillRetry: willRetry));
            throw new CodeGenerationActivityException(
                exception.Message,
                exception,
                errorType: exception.GetType().Name,
                nonRetryable: !transient);
        }
    }

    private static async Task<CodeGenerationTaskRetryContext?>
        ReadPreviousAttemptAsync(CodeGenerationActivityInfo info)
    {
        if (info.HeartbeatDetails.Count == 0)
            return null;

        return await info
            .HeartbeatDetailAtAsync<CodeGenerationTaskRetryContext>(0);
    }

    private static CodeGenerationTaskRetryContext? MapCorrection(
        CodeGenerationBuildCorrection? correction) =>
        correction is null
            ? null
            : new CodeGenerationTaskRetryContext(
                correction.PreviousAttempt,
                correction.PreviousModel,
                correction.Failure,
                correction.Error,
                correction.Diagnostics,
                correction.WrittenFiles);

    private static string CreateStartedMessage(
        int attempt,
        int maximumAttempts,
        CodeGenerationModelSelection selection,
        CodeGenerationTaskRetryContext? previousAttempt)
    {
        if (attempt == 1)
        {
            return $"Task generation started with {selection.Model}.";
        }

        var escalated = previousAttempt is not null &&
            !previousAttempt.PreviousModel.Equals(
                selection.Model,
                StringComparison.OrdinalIgnoreCase);
        var action = escalated
            ? $"Model escalated from {previousAttempt!.PreviousModel} to {selection.Model}"
            : $"Retry started with {selection.Model}";

        return $"{action}; overall attempt {attempt} of {maximumAttempts}, model attempt {selection.ModelAttempt} of {CodeGenerationWorkflowConstants.MaximumAttemptsPerModel}. Previous failure diagnostics were included.";
    }

    private async Task PublishSafelyAsync(
        string workflowId,
        WorkflowProgress progress)
    {
        try
        {
            await progressPublisher.PublishAsync(
                workflowId,
                progress,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to publish progress for task {TaskId} in workflow {WorkflowId}.",
                progress.Metadata?.GetValueOrDefault("taskId"),
                workflowId);
        }
    }

    private static string CreateFailureMessage(
        string? error,
        bool willRetry) =>
        willRetry
            ? $"{error ?? "Task generation failed."} A retry will start automatically."
            : error ?? "Task generation failed.";

    private static CodeGenerationTaskWorkflowResult Map(
        string taskId,
        CodeGenerationOutcome outcome,
        string outputRoot,
        int modelTier,
        bool isBuildRepair,
        int buildRepairCycle) =>
        new CodeGenerationTaskWorkflowResult(
            taskId,
            outcome.Succeeded,
            outcome.Failure.ToString(),
            outcome.Error,
            outcome.Model,
            outcome.JsonWasRepaired,
            outcome.JsonRepairAttempts,
            ToRelativePaths(outputRoot, outcome.WrittenFiles),
            outcome.SkippedFiles,
            outcome.Usage is null
                ? null
                : new CodeGenerationUsage(
                    outcome.Usage.PromptTokens,
                    outcome.Usage.CompletionTokens,
                    outcome.Usage.TotalTokens,
                    outcome.Usage.PromptCacheHitTokens,
                    outcome.Usage.PromptCacheMissTokens),
            outcome.Diagnostics is null
                ? null
                : new CodeGenerationDiagnostics(
                    outcome.Diagnostics.Provider,
                    outcome.Diagnostics.ActualModel,
                    outcome.Diagnostics.Api,
                    outcome.Diagnostics.Done,
                    outcome.Diagnostics.DoneReason,
                    outcome.Diagnostics.TotalDurationMilliseconds,
                    outcome.Diagnostics.LoadDurationMilliseconds,
                    outcome.Diagnostics.PromptEvaluationDurationMilliseconds,
                    outcome.Diagnostics.GenerationDurationMilliseconds,
                    outcome.Diagnostics.GenerationTokensPerSecond,
                    outcome.Diagnostics.NativeToolCallCount,
                    outcome.Diagnostics.ContentLength),
            outcome.FinishReason)
        {
            ModelTier = modelTier,
            IsBuildRepair = isBuildRepair,
            BuildRepairCycle = buildRepairCycle
        };

    private static IReadOnlyList<string> ToRelativePaths(
        string outputRoot,
        IEnumerable<string> paths)
    {
        var fullRoot = Path.GetFullPath(outputRoot);
        return paths
            .Select(Path.GetFullPath)
            .Select(path => Path.GetRelativePath(fullRoot, path))
            .Where(path => path != ".." &&
                !path.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .Select(path => path.Replace('\\', '/'))
            .ToArray();
    }
}
