using Microsoft.Extensions.Options;
using Penghou.Cangjie;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.CodeGeneration;
using Guyabano.Llm.Prompting;
using Guyabano.Messaging;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationTaskActivities(
    ICodeGenerationTaskService taskService,
    IArtifactRepository artifactRepository,
    IContextStore contextStore,
    IWorkflowProgressPublisher progressPublisher,
    IOptions<CodeGenerationWorkerOptions> options,
    CodeGenerationWorkspaceResolver workspaceResolver,
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
        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            workflowId,
            context.CancellationToken);
        var workspaceRoot = workspace.HostPath;
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

        IReadOnlyDictionary<string, (string Hash, long Length)>? beforeSnapshot = null;
        IDisposable? generationCorrelation = null;
        try
        {
            var taskContext =
                await CodeGenerationTaskContextFactory.CreateAsync(
                    request.Plan,
                    request.ParentTaskId,
                    request.Task,
                    workspaceRoot,
                    context.CancellationToken);
            taskContext = taskContext with
            {
                SessionId = workspace.SessionId.ToString(),
                WorkflowRunId = workflowId,
                WorkflowStepKey = info.ActivityId
            };
            if (previousAttempt is not null)
            {
                taskContext = taskContext with
                {
                    Retry = previousAttempt
                };
            }

            beforeSnapshot = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(
                workspace.HostPath,
                context.CancellationToken);

            ContextSnapshot? generationSnapshot = null;
            try
            {
                generationSnapshot = await CangjieSnapshotHelper.EnsureSnapshotAsync(
                    contextStore,
                    workspace.SessionId.ToString(),
                    workflowId,
                    info.ActivityId,
                    CodeGenerationZhinuStepScope.Current?.Revision ?? 1,
                    queryIdentity: $"guyabano:{workflowId}:generation:{task.Id}",
                    strategy: "code-generation",
                    strategyVersion: "1",
                    purpose: request.IsBuildRepair ? "code-generation-build-repair" : "code-generation",
                    workspaceRevision: null,
                    hetuIndexRunId: request.RepositoryContext?.Revision.IndexRunId,
                    hetuIndexIdentity: request.RepositoryContext?.Revision.WorkspaceRevision,
                    itemIds: [],
                    cancellationToken: context.CancellationToken);
                generationCorrelation = LlmRequestCorrelationScope.Push(new(
                    workspace.SessionId.ToString(),
                    workflowId,
                    info.ActivityId,
                    CangjieSnapshotId: generationSnapshot.Id,
                    CangjieStrategy: generationSnapshot.Strategy,
                    CangjieStrategyVersion: generationSnapshot.StrategyVersion,
                    CangjieQueryIdentity: generationSnapshot.QueryIdentity,
                    CangjiePurpose: generationSnapshot.Purpose,
                    HetuIndexRunId: generationSnapshot.Metadata.TryGetValue("hetuIndexRunId", out var hetuRun) ? hetuRun : request.RepositoryContext?.Revision.IndexRunId,
                    HetuIndexIdentity: generationSnapshot.Metadata.TryGetValue("hetuIndexIdentity", out var hetuId) ? hetuId : request.RepositoryContext?.Revision.WorkspaceRevision,
                    WorkspaceRevision: generationSnapshot.Metadata.TryGetValue("workspaceRevision", out var wsRev) ? wsRev : null,
                    WorkflowStepRevision: CodeGenerationZhinuStepScope.Current?.Revision));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to create Cangjie snapshot for generation {TaskId} workflow {WorkflowId}", task.Id, workflowId);
            }

            var outcome = await taskService.GenerateAndEmitAsync(
                taskContext,
                workspaceRoot,
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
                generationCorrelation?.Dispose();
                context.Heartbeat(
                    CodeGenerationRetryContextFactory.Create(
                        outcome,
                        attempt,
                        actualModel,
                        workspaceRoot));
                throw new CodeGenerationActivityException(
                    outcome.Error ?? $"Task '{task.Id}' failed.",
                    errorType: outcome.Failure.ToString(),
                    nonRetryable: false);
            }

            if (outcome.Succeeded)
            {
                await WriteGenerationArtifactsAsync(
                    workflowId,
                    workspace,
                    info,
                    request,
                    outcome,
                    taskContext,
                    selection,
                    beforeSnapshot!,
                    context.CancellationToken);
            }

            generationCorrelation?.Dispose();

            return Map(
                task.Id,
                outcome,
                workspaceRoot,
                selection.Tier,
                request.IsBuildRepair,
                request.BuildRepairCycle);
        }
        catch (CodeGenerationActivityException)
        {
            generationCorrelation?.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            generationCorrelation?.Dispose();
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
            generationCorrelation?.Dispose();
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

    private async Task WriteGenerationArtifactsAsync(
        string workflowId,
        CodeGenerationWorkspace workspace,
        CodeGenerationActivityInfo info,
        CodeGenerationTaskWorkflowRequest request,
        CodeGenerationOutcome outcome,
        CodeGenerationTaskContext taskContext,
        CodeGenerationModelSelection selection,
        IReadOnlyDictionary<string, (string Hash, long Length)> beforeSnapshot,
        CancellationToken cancellationToken)
    {
        var zhinuContext = CodeGenerationZhinuStepScope.Current;
        var stepKey = zhinuContext?.StepKey ?? info.ActivityId;
        var stepRevision = zhinuContext?.Revision ?? info.Attempt;

        var payload = new TaskContextArtifactPayload(
            Context: taskContext,
            SessionId: workspace.SessionId.ToString(),
            WorkflowRunId: workflowId,
            StepKey: stepKey,
            StepRevision: stepRevision,
            CangjieSnapshotId: request.RepositoryContext?.SnapshotId,
            CangjieStrategy: request.RepositoryContext?.Strategy,
            CangjieStrategyVersion: request.RepositoryContext?.StrategyVersion,
            HetuIndexRunId: request.RepositoryContext?.Revision.IndexRunId,
            HetuIndexIdentity: request.RepositoryContext?.Revision.WorkspaceRevision,
            HetuProviderSnapshotIdentity: request.RepositoryContext?.Revision.ProviderSnapshotIdentity,
            RetryContext: taskContext.Retry);

        var taskContextEnvelope = await artifactRepository.WriteAsync(
            new ArtifactWriteRequest<TaskContextArtifactPayload>(
                WorkflowId: workflowId,
                Kind: "task-context",
                SchemaVersion: 1,
                StageKey: request.Task.Id,
                Status: ArtifactStatus.Validated,
                Payload: payload),
            cancellationToken).ConfigureAwait(false);

        var usage = outcome.Usage is null
            ? null
            : new CodeGenerationUsage(
                outcome.Usage.PromptTokens,
                outcome.Usage.CompletionTokens,
                outcome.Usage.TotalTokens,
                outcome.Usage.PromptCacheHitTokens,
                outcome.Usage.PromptCacheMissTokens);
        var diagnostics = outcome.Diagnostics is null
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
                outcome.Diagnostics.ContentLength);

        var afterSnapshot = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(
            workspace.HostPath,
            cancellationToken).ConfigureAwait(false);
        var previousManifest = await artifactRepository.ReadLatestAsync<GeneratedFileManifest>(
            workflowId,
            "generated-file-manifest",
            request.Task.Id,
            cancellationToken).ConfigureAwait(false);
        var previousPayload = previousManifest?.Payload;
        var currentOwnedPaths = ToRelativePathSet(
            workspace.HostPath,
            outcome.WrittenFiles.Concat(outcome.SkippedFiles));
        var relevantPaths = new HashSet<string>(
            currentOwnedPaths,
            StringComparer.Ordinal);
        if (previousPayload is not null)
        {
            relevantPaths.UnionWith(previousPayload.Files.Select(file =>
                file.RelativePath));
            relevantPaths.UnionWith((previousPayload.StaleFiles ?? [])
                .Select(file => file.RelativePath));
        }
        var taskBeforeSnapshot = beforeSnapshot
            .Where(pair => relevantPaths.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var taskAfterSnapshot = afterSnapshot
            .Where(pair => relevantPaths.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        await EnsureNoOwnershipConflictsAsync(
            workflowId,
            request.Task.Id,
            currentOwnedPaths,
            cancellationToken).ConfigureAwait(false);

        var manifest = await GeneratedFileManifestFactory.CreateWithWorkspaceDiffAsync(
            sessionId: workspace.SessionId.ToString(),
            workflowRunId: workflowId,
            stepKey: stepKey,
            stepRevision: stepRevision,
            workspaceHostPath: workspace.HostPath,
            workspaceCiPath: workspace.CiRelativePath,
            taskId: request.Task.Id,
            beforeSnapshot: taskBeforeSnapshot,
            afterSnapshot: taskAfterSnapshot,
            previousManifest: previousPayload,
            skippedFiles: outcome.SkippedFiles,
            currentOwnedPaths: currentOwnedPaths,
            parentTaskId: request.ParentTaskId,
            isBuildRepair: request.IsBuildRepair,
            buildRepairCycle: request.BuildRepairCycle,
            model: outcome.Diagnostics?.ActualModel ?? outcome.Model,
            modelTier: selection.Tier,
            usage: usage,
            diagnostics: diagnostics,
            finishReason: outcome.FinishReason,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await artifactRepository.WriteAsync(
            new ArtifactWriteRequest<GeneratedFileManifest>(
                WorkflowId: workflowId,
                Kind: "generated-file-manifest",
                SchemaVersion: 2,
                StageKey: request.Task.Id,
                Status: ArtifactStatus.Validated,
                Payload: manifest,
                Inputs: [taskContextEnvelope.Reference]),
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreateFailureMessage(
        string? error,
        bool willRetry) =>
        willRetry
            ? $"{error ?? "Task generation failed."} A retry will start automatically."
            : error ?? "Task generation failed.";

    private async Task EnsureNoOwnershipConflictsAsync(
        string workflowId,
        string taskId,
        IReadOnlySet<string> currentOwnedPaths,
        CancellationToken cancellationToken)
    {
        if (currentOwnedPaths.Count == 0)
            return;

        var hits = await contextStore.SearchAsync(
            new ContextQuery
            {
                Text = workflowId,
                Scope = ContextIndexingArtifactRepository.DefaultScope,
                Kinds = [ContextKinds.Artifact],
                Limit = 1000,
                SearchMode = ContextSearchMode.AnyTerm
            },
            cancellationToken).ConfigureAwait(false);
        foreach (var hit in hits)
        {
            GeneratedFileManifest? manifest;
            try
            {
                manifest = System.Text.Json.JsonSerializer
                    .Deserialize<GeneratedFileManifest>(
                        hit.Item.Content,
                        new System.Text.Json.JsonSerializerOptions(
                            System.Text.Json.JsonSerializerDefaults.Web));
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (manifest is null ||
                !manifest.WorkflowRunId.Equals(
                    workflowId,
                    StringComparison.Ordinal) ||
                manifest.TaskId.Equals(taskId, StringComparison.Ordinal))
            {
                continue;
            }

            var conflict = manifest.Files.FirstOrDefault(file =>
                file.Operation is not "Deleted" and not "Stale" &&
                currentOwnedPaths.Contains(file.RelativePath));
            if (conflict is not null)
            {
                throw new InvalidOperationException(
                    $"Generated file '{conflict.RelativePath}' is already owned by " +
                    $"task '{manifest.TaskId}' and cannot also be claimed by " +
                    $"task '{taskId}'.");
            }
        }
    }

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

    private static IReadOnlySet<string> ToRelativePathSet(
        string outputRoot,
        IEnumerable<string> paths)
    {
        var fullRoot = Path.GetFullPath(outputRoot);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in paths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(
                    Path.IsPathRooted(candidate)
                        ? candidate
                        : Path.Combine(fullRoot, candidate));
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException)
            {
                continue;
            }

            var relative = Path.GetRelativePath(fullRoot, fullPath);
            if (relative == ".." ||
                relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                continue;
            }

            result.Add(relative.Replace('\\', '/'));
        }

        return result;
    }
}
