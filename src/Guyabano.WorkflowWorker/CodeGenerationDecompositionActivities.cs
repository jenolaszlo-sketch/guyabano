using Microsoft.Extensions.Options;
using Penghou.Baize;
using Penghou.Cangjie;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.Prompting;
using Guyabano.Messaging;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationDecompositionActivities(
    ICodeGenerationTaskDecompositionService decompositionService,
    IResolvedDependencyContextBuilder dependencyContextBuilder,
    IComponentWorkContextBuilder workContextBuilder,
    IArtifactRepository artifactRepository,
    IContextStore contextStore,
    IWorkflowProgressPublisher progressPublisher,
    IOptions<CodeGenerationWorkerOptions> options,
    CodeGenerationWorkspaceResolver workspaceResolver,
    ILogger<CodeGenerationDecompositionActivities> logger)
{
    public async Task<CodeGenerationDecompositionWorkflowResult>
        DecomposeAsync(CodeGenerationDecompositionWorkflowRequest request)
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
        var assembledContext = settings.IncludeRepositoryContextInPrompts
            ? SessionContextAssembler.Assemble(
                request.RepositoryContext,
                "code-generation-decomposition",
                settings.RepositoryContextMaximumPromptCharacters)
            : null;
        using var disclosureScope = SessionContextDisclosureScope.Push(
            assembledContext?.Content);
        ContextSnapshot? decompositionSnapshot = null;
        if (request.RepositoryContext is not null)
        {
            try
            {
                var sourceSnapshot = await contextStore.GetSnapshotAsync(
                    request.RepositoryContext.SnapshotId,
                    context.CancellationToken).ConfigureAwait(false);
                if (sourceSnapshot is null)
                {
                    logger.LogWarning(
                        "Repository Cangjie snapshot {SnapshotId} was not found for decomposition {ParentId} workflow {WorkflowId}; provenance will retain the referenced source identity.",
                        request.RepositoryContext.SnapshotId,
                        request.ParentTaskId,
                        workflowId);
                }
                else
                {
                    decompositionSnapshot = await CangjieSnapshotHelper.EnsureSnapshotAsync(
                        contextStore,
                        workspace.SessionId.ToString(),
                        workflowId,
                        info.ActivityId,
                        CodeGenerationZhinuStepScope.Current?.Revision ?? 1,
                        queryIdentity: $"guyabano:{workflowId}:decomposition:{request.ParentTaskId}",
                        strategy: "decomposition-input-closure",
                        strategyVersion: "2",
                        purpose: "code-generation-decomposition",
                        workspaceRevision: request.RepositoryContext.Revision.WorkspaceRevision,
                        hetuIndexRunId: request.RepositoryContext.Revision.IndexRunId,
                        hetuIndexIdentity: request.RepositoryContext.Revision.WorkspaceRevision,
                        itemIds: sourceSnapshot.ItemIds,
                        cancellationToken: context.CancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to derive the Cangjie input snapshot for decomposition {ParentId} workflow {WorkflowId}", request.ParentTaskId, workflowId);
            }
        }

        using var correlationScope = decompositionSnapshot is not null
            ? LlmRequestCorrelationScope.Push(new(
                workspace.SessionId.ToString(),
                workflowId,
                info.ActivityId,
                CangjieSnapshotId: decompositionSnapshot.Id,
                CangjieStrategy: decompositionSnapshot.Strategy,
                CangjieStrategyVersion: decompositionSnapshot.StrategyVersion,
                CangjieQueryIdentity: decompositionSnapshot.QueryIdentity,
                CangjiePurpose: decompositionSnapshot.Purpose,
                HetuIndexRunId: request.RepositoryContext!.Revision.IndexRunId,
                HetuIndexIdentity: request.RepositoryContext.Revision.WorkspaceRevision,
                WorkspaceRevision: request.RepositoryContext.Revision.WorkspaceRevision,
                WorkflowStepRevision: CodeGenerationZhinuStepScope.Current?.Revision))
            : request.RepositoryContext is not null
            ? LlmRequestCorrelationScope.Push(new(
                workspace.SessionId.ToString(),
                workflowId,
                info.ActivityId,
                CangjieSnapshotId: request.RepositoryContext.SnapshotId,
                CangjieStrategy: request.RepositoryContext.Strategy,
                CangjieStrategyVersion: request.RepositoryContext.StrategyVersion,
                CangjiePurpose: "code-generation-decomposition",
                HetuIndexRunId: request.RepositoryContext.Revision.IndexRunId,
                HetuIndexIdentity: request.RepositoryContext.Revision.WorkspaceRevision,
                WorkspaceRevision: request.RepositoryContext.Revision.WorkspaceRevision,
                WorkflowStepRevision: CodeGenerationZhinuStepScope.Current?.Revision))
            : LlmRequestCorrelationScope.Push(new(
                workspace.SessionId.ToString(),
                workflowId,
                info.ActivityId));
        var parent = request.Plan.Tasks.Single(item => item.Id.Equals(
            request.ParentTaskId,
            StringComparison.Ordinal));
        var attempt = info.Attempt;
        var maximumAttempts =
            DecompositionModelSelector.MaximumAttempts(settings);
        var selection = DecompositionModelSelector.Select(
            settings,
            attempt);
        var maxTokens = selection.ModelAttempt == 1
            ? settings.DecompositionMaxTokens
            : settings.DecompositionRetryMaxTokens;
        var stage = $"Decompose {parent.Id}: {parent.Title}";

        await PublishSafelyAsync(workflowId, new WorkflowProgress(
            WorkflowProgressEventType.Started,
            stage,
            CreateStartedMessage(
                attempt,
                maximumAttempts,
                selection,
                maxTokens),
            DateTimeOffset.UtcNow,
            RunId: info.WorkflowRunId,
            ActivityId: info.ActivityId,
            Attempt: attempt,
            Model: selection.Model,
            MaximumAttempts: maximumAttempts));

        try
        {
            var upstream = await LoadUpstreamArtifactsAsync(
                request.UpstreamDecompositionArtifacts,
                context.CancellationToken);
            var dependencyContext = dependencyContextBuilder.Build(
                request.Plan,
                request.ParentTaskId,
                upstream.Select(item => item.Payload).ToArray());
            var workContext = workContextBuilder.Build(
                request.Plan,
                request.ParentTaskId,
                dependencyContext);
            var usedTaskIds = dependencyContext.Artifacts
                .Select(item => item.ArchitectureTaskId)
                .ToHashSet(StringComparer.Ordinal);
            var workContextInputs = upstream
                .Where(item => usedTaskIds.Contains(
                    item.Payload.ParentTaskId))
                .Select(item => item.Reference)
                .Concat(request.ArchitectureArtifact is null
                    ? []
                    : [request.ArchitectureArtifact])
                .ToArray();
            var workContextEnvelope = await artifactRepository.WriteAsync(
                new ArtifactWriteRequest<ComponentWorkContext>(
                    WorkflowId: workflowId,
                    Kind: "component-work-context",
                    SchemaVersion: 1,
                    StageKey: parent.Id,
                    Status: ArtifactStatus.Validated,
                    Payload: workContext,
                    Inputs: workContextInputs),
                context.CancellationToken);
            var outcome = await decompositionService.DecomposeAsync(
                workContext,
                selection.Model,
                maxTokens,
                context.CancellationToken);
            var actualModel = outcome.Diagnostics?.ActualModel ?? outcome.Model;
            var decomposition = outcome.Decomposition;
            var hasGap = decomposition?.Status ==
                TaskDecompositionStatus.ArchitectureGap;
            var succeeded = outcome.Succeeded &&
                decomposition is not null &&
                !hasGap;
            var willRetry = !outcome.Succeeded &&
                attempt < maximumAttempts;
            var error = hasGap
                ? string.Join(
                    " ",
                    decomposition!.ArchitectureGaps.Select(gap =>
                        $"{gap.Question} {gap.Reason}"))
                : outcome.Error;
            ArtifactReference? artifact = null;
            if (succeeded)
            {
                var envelope = await artifactRepository.WriteAsync(
                    new ArtifactWriteRequest<
                        TaskDecompositionArtifactPayload>(
                        WorkflowId: workflowId,
                        Kind: "task-decomposition",
                        SchemaVersion: 1,
                        StageKey: parent.Id,
                        Status: ArtifactStatus.Validated,
                        Payload: new(
                            parent.Id,
                            decomposition!,
                            request.ArchitectureVersion),
                        Inputs: [workContextEnvelope.Reference]),
                    context.CancellationToken);
                artifact = envelope.Reference;
            }

            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                succeeded
                    ? WorkflowProgressEventType.Completed
                    : WorkflowProgressEventType.Failed,
                stage,
                succeeded
                    ? $"{parent.Id} was decomposed into {decomposition!.LeafTasks.Count} execution-ready leaf task(s)."
                    : hasGap
                        ? $"Decomposition found an architecture gap: {error}"
                        : willRetry
                            ? $"{error} A decomposition retry will start automatically."
                            : error ?? "Task decomposition failed.",
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: attempt,
                Model: actualModel,
                GeneratedTokens: outcome.Usage?.CompletionTokens,
                Succeeded: succeeded,
                Metadata: new Dictionary<string, string>
                {
                    ["parentTaskId"] = parent.Id,
                    ["architectureVersion"] =
                        request.ArchitectureVersion.ToString(),
                    ["status"] = decomposition?.Status.ToString() ??
                        outcome.Failure.ToString(),
                    ["leafCount"] = decomposition?.LeafTasks.Count.ToString() ?? "0",
                    ["totalLeafPoints"] = decomposition?.LeafTasks
                        .Sum(item => item.ComplexityPoints)
                        .ToString() ?? "0",
                    ["jsonWasRepaired"] = outcome.JsonWasRepaired.ToString(),
                    ["maxTokens"] = maxTokens.ToString(),
                    ["modelTier"] = selection.Tier.ToString(),
                    ["modelAttempt"] = selection.ModelAttempt.ToString(),
                    ["maximumModelAttempts"] =
                        CodeGenerationWorkflowConstants
                            .MaximumAttemptsPerModel
                            .ToString(),
                    ["artifactId"] = artifact?.ArtifactId ?? string.Empty,
                    ["failureFingerprint"] = succeeded
                        ? string.Empty
                        : FailureFingerprint.Create(
                            outcome.Failure.ToString(),
                            error)
                },
                Diagnostics: CreateDiagnostics(outcome, error),
                MaximumAttempts: maximumAttempts,
                WillRetry: willRetry));

            if (willRetry)
            {
                throw new CodeGenerationActivityException(
                    error ?? "Task decomposition failed.",
                    errorType: outcome.Failure.ToString(),
                    nonRetryable: false);
            }

            return Map(
                parent.Id,
                outcome,
                succeeded,
                hasGap ? "ArchitectureGap" : outcome.Failure.ToString(),
                error,
                artifact) with
            {
                ArchitectureVersion = request.ArchitectureVersion,
                ArchitectureArtifact = request.ArchitectureArtifact
            };
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
                "Task decomposition was canceled.",
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: attempt,
                Model: selection.Model,
                Succeeded: false,
                MaximumAttempts: maximumAttempts,
                WillRetry: false));
            throw;
        }
        catch (Exception exception)
        {
            var transient = ActivityExceptionClassifier.IsTransient(exception);
            var willRetry = transient && attempt < maximumAttempts;
            logger.LogError(
                exception,
                "Unexpected decomposition failure for task {TaskId}.",
                parent.Id);
            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                WorkflowProgressEventType.Failed,
                stage,
                willRetry
                    ? $"{exception.Message} Decomposition will retry automatically."
                    : exception.Message,
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: attempt,
                Model: selection.Model,
                Succeeded: false,
                Metadata: new Dictionary<string, string>
                {
                    ["parentTaskId"] = parent.Id,
                    ["exceptionType"] = exception.GetType().FullName ??
                        exception.GetType().Name,
                    ["failureKind"] = "UnhandledActivityException"
                },
                Diagnostics:
                [
                    new WorkflowDiagnostic(
                        WorkflowDiagnosticSeverity.Error,
                        "decomposition-activity-exception",
                        "Task decomposition failed unexpectedly.",
                        [exception.Message])
                ],
                MaximumAttempts: maximumAttempts,
                WillRetry: willRetry));
            throw new CodeGenerationActivityException(
                exception.Message,
                exception,
                exception.GetType().Name,
                nonRetryable: !transient);
        }
    }

    private async Task<IReadOnlyList<ArtifactEnvelope<
        TaskDecompositionArtifactPayload>>> LoadUpstreamArtifactsAsync(
        IReadOnlyList<ArtifactReference> references,
        CancellationToken cancellationToken)
    {
        var result = new List<ArtifactEnvelope<
            TaskDecompositionArtifactPayload>>();
        foreach (var reference in references)
        {
            var artifact = await artifactRepository.ReadAsync<
                TaskDecompositionArtifactPayload>(
                reference,
                cancellationToken);
            if (artifact is null)
            {
                throw new ArtifactIntegrityException(
                    $"Upstream artifact '{reference.ArtifactId}' was not found.");
            }

            if (artifact.Status != ArtifactStatus.Validated &&
                artifact.Status != ArtifactStatus.Approved)
            {
                throw new ArtifactIntegrityException(
                    $"Upstream artifact '{reference.ArtifactId}' is not validated.");
            }

            result.Add(artifact);
        }

        return result;
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
                "Unable to publish decomposition progress for {WorkflowId}.",
                workflowId);
        }
    }

    private static string CreateStartedMessage(
        int attempt,
        int maximumAttempts,
        DecompositionModelSelection selection,
        int maxTokens)
    {
        if (attempt == 1)
        {
            return $"Target decomposition started with {selection.Model} using a {maxTokens:N0}-token allowance.";
        }

        var action = selection.ModelAttempt == 1
            ? $"Decomposition model escalated to {selection.Model}"
            : $"Target decomposition retry started with {selection.Model}";
        return $"{action}; overall attempt {attempt} of {maximumAttempts}, model attempt {selection.ModelAttempt} of {CodeGenerationWorkflowConstants.MaximumAttemptsPerModel}, using a {maxTokens:N0}-token allowance.";
    }

    private static IReadOnlyList<WorkflowDiagnostic> CreateDiagnostics(
        CodeGenerationDecompositionOutcome outcome,
        string? error)
    {
        var result = new List<WorkflowDiagnostic>();
        if (outcome.JsonWasRepaired)
        {
            result.Add(new(
                WorkflowDiagnosticSeverity.Warning,
                "decomposition-json-repaired",
                "The decomposition response required JSON repair.",
                outcome.JsonRepairAttempts
                    .Where(item => item.Status is not
                        LlmRepairStatus.Skipped and not
                        LlmRepairStatus.NotApplicable)
                    .Select(item => $"{item.Name}: {item.Status}")
                    .ToArray()));
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            var evidence = FailureFingerprint.Evidence(
                outcome.Failure.ToString(),
                error);
            result.Add(new(
                WorkflowDiagnosticSeverity.Error,
                "decomposition-failed",
                "The target decomposition did not produce executable leaves.",
                new[] { error }.Concat(evidence).ToArray()));
        }

        return result;
    }

    private static CodeGenerationDecompositionWorkflowResult Map(
        string parentTaskId,
        CodeGenerationDecompositionOutcome outcome,
        bool succeeded,
        string failure,
        string? error,
        ArtifactReference? artifact) =>
        new(
            parentTaskId,
            succeeded,
            failure,
            error,
            outcome.Model,
            outcome.Decomposition,
            outcome.JsonWasRepaired,
            outcome.JsonRepairAttempts,
            outcome.Usage is null
                ? null
                : new(
                    outcome.Usage.PromptTokens,
                    outcome.Usage.CompletionTokens,
                    outcome.Usage.TotalTokens,
                    outcome.Usage.PromptCacheHitTokens,
                    outcome.Usage.PromptCacheMissTokens),
            outcome.Diagnostics is null
                ? null
                : new(
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
            outcome.FinishReason,
            artifact);
}
