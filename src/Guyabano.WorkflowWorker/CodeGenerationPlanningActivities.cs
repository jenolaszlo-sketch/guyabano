using Microsoft.Extensions.Options;
using Penghou.Baize;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.Prompting;
using Guyabano.Messaging;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationPlanningActivities(
    ICodeGenerationPlanningService planningService,
    IArtifactRepository artifactRepository,
    IWorkflowProgressPublisher progressPublisher,
    IOptions<CodeGenerationWorkerOptions> options,
    ILogger<CodeGenerationPlanningActivities> logger)
{
    public async Task<CodeGenerationWorkflowResult> PlanAsync(
        CodeGenerationWorkflowRequest request)
    {
        var settings = options.Value;
        var context = CodeGenerationActivityExecutionContext.Current;
        var info = context.Info;
        var workflowId = info.WorkflowId ??
            throw new InvalidOperationException(
                "Workflow activity information did not include a workflow ID.");
        var repositorySnapshot = request.RepositoryContext;
        // Bounded disclosure preview: log what will be sent to the model
        if (repositorySnapshot is not null)
        {
            var previewLength = repositorySnapshot.Content.Length;
            var truncated = previewLength > settings.RepositoryContextMaximumPromptCharacters;
            logger.LogInformation(
                "Disclosure preview for planning {WorkflowId} step {StepKey}: Cangjie snapshot {SnapshotId} strategy {Strategy}v{Version} query {Query} purpose {Purpose} Hetu run {HetuRun} workspace {Workspace} length {Length} truncated {Truncated}",
                workflowId, info.ActivityId, repositorySnapshot.SnapshotId, repositorySnapshot.Strategy, repositorySnapshot.StrategyVersion,
                $"guyabano:{workflowId}:repository-context", "code-generation-planning", repositorySnapshot.Revision.IndexRunId, repositorySnapshot.Revision.WorkspaceRevision,
                previewLength, truncated);
        }

        using var correlationScope = LlmRequestCorrelationScope.Push(new(
            request.SessionId.ToString(),
            workflowId,
            info.ActivityId,
            CangjieSnapshotId: repositorySnapshot?.SnapshotId,
            CangjieStrategy: repositorySnapshot?.Strategy,
            CangjieStrategyVersion: repositorySnapshot?.StrategyVersion,
            CangjieQueryIdentity: repositorySnapshot is not null ? $"guyabano:{workflowId}:repository-context" : null,
            CangjiePurpose: repositorySnapshot is not null ? "code-generation-planning" : null,
            HetuIndexRunId: repositorySnapshot?.Revision.IndexRunId,
            HetuIndexIdentity: repositorySnapshot?.Revision.WorkspaceRevision,
            WorkspaceRevision: repositorySnapshot?.Revision.WorkspaceRevision,
            WorkflowStepRevision: CodeGenerationZhinuStepScope.Current?.Revision));
        var transportAttempt = info.Attempt;
        const int maximumAttempts =
            CodeGenerationWorkflowConstants.MaximumPlanningTransportAttempts;

        await PublishSafelyAsync(
            workflowId,
            new WorkflowProgress(
                EventType: WorkflowProgressEventType.Started,
                Stage: "Planning",
                Message: transportAttempt > 1
                    ? $"Planning transport retry {transportAttempt} of {maximumAttempts} started with {settings.PlannerModel}."
                    : $"Planning started with {settings.PlannerModel}.",
                Timestamp: DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: transportAttempt,
                Model: settings.PlannerModel,
                MaximumAttempts: maximumAttempts));

        try
        {
            var retryState = info.HeartbeatDetails.Count == 0
                ? new PlanningModelRetryState()
                : await info.HeartbeatDetailAtAsync<PlanningModelRetryState>(0);
            var currentModelAttempt = retryState.TotalFailures + 1;
            var currentMaximumModelAttempts =
                CodeGenerationWorkflowConstants
                    .MaximumArchitectureModelOutputAttempts;
            CodeGenerationPlanningOutcome outcome;
            while (true)
            {
                outcome = await planningService.PlanAsync(
                    BuildPlanningRequest(request, settings),
                    settings.PlannerModel,
                    settings.PlannerMaxTokens,
                    retryState.PreviousFailure,
                    context.CancellationToken);
                if (outcome.Succeeded && outcome.Plan is not null)
                    break;

                var failureKind = PlanningModelRetryPolicy.Classify(
                    outcome.Failure);
                var modelAttempt = retryState.Attempt(failureKind);
                var maximumModelAttempts =
                    PlanningModelRetryPolicy.MaximumAttempts(failureKind);
                currentModelAttempt = modelAttempt;
                currentMaximumModelAttempts = maximumModelAttempts;
                var willRetry = modelAttempt < maximumModelAttempts;
                var actualFailureModel = outcome.Diagnostics?.ActualModel ??
                    outcome.Model;
                var metadata = new Dictionary<string, string>(
                    CreateMetadata(outcome))
                {
                    ["retryKind"] = failureKind.ToString(),
                    ["transportAttempt"] = transportAttempt.ToString()
                };

                await PublishSafelyAsync(
                    workflowId,
                    new WorkflowProgress(
                        EventType: WorkflowProgressEventType.Failed,
                        Stage: outcome.Failure.ToString(),
                        Message: willRetry
                            ? $"{outcome.Error} A {failureKind.ToString().ToLowerInvariant()} model retry will start automatically."
                            : outcome.Error ?? "Planning failed.",
                        Timestamp: DateTimeOffset.UtcNow,
                        RunId: info.WorkflowRunId,
                        ActivityId: info.ActivityId,
                        Attempt: modelAttempt,
                        Model: actualFailureModel,
                        GeneratedTokens: outcome.Usage?.CompletionTokens,
                        Succeeded: false,
                        Metadata: metadata,
                        Diagnostics: CreateDiagnostics(outcome, true),
                        MaximumAttempts: maximumModelAttempts,
                        WillRetry: willRetry));

                if (!willRetry)
                    return Map(outcome);

                retryState = retryState.Record(
                    failureKind,
                    outcome.Error ?? outcome.Failure.ToString());
                context.Heartbeat(retryState);
                currentModelAttempt = modelAttempt + 1;

                await PublishSafelyAsync(
                    workflowId,
                    new WorkflowProgress(
                        EventType: WorkflowProgressEventType.Started,
                        Stage: "Planning",
                        Message:
                            $"Planning {failureKind.ToString().ToLowerInvariant()} " +
                            $"retry {currentModelAttempt} of {maximumModelAttempts} " +
                            $"started with {settings.PlannerModel}.",
                        Timestamp: DateTimeOffset.UtcNow,
                        RunId: info.WorkflowRunId,
                        ActivityId: info.ActivityId,
                        Attempt: currentModelAttempt,
                        Model: settings.PlannerModel,
                        Metadata: new Dictionary<string, string>
                        {
                            ["retryKind"] = failureKind.ToString(),
                            ["transportAttempt"] = transportAttempt.ToString()
                        },
                        MaximumAttempts: maximumModelAttempts,
                        WillRetry: false));
            }
            var actualModel = outcome.Diagnostics?.ActualModel ??
                outcome.Model;
            var planningArtifacts = outcome.StagedArtifacts is null
                ? []
                : await WritePlanningArtifactsAsync(
                    workflowId,
                    outcome.StagedArtifacts,
                    context.CancellationToken);

            var highComplexityTasks = outcome.Plan.Tasks.Count(task =>
                task.DecompositionRecommended ||
                task.ComplexityPoints >= 8);

            await PublishSafelyAsync(
                workflowId,
                new WorkflowProgress(
                    EventType: WorkflowProgressEventType.Completed,
                    Stage: "Plan completed",
                    Message:
                        $"Planning produced {outcome.Plan.Tasks.Count} task(s), " +
                        $"{outcome.Plan.Tasks.Sum(task => task.ComplexityPoints)} total points, " +
                        $"and {highComplexityTasks} task(s) recommended for further decomposition.",
                    Timestamp: DateTimeOffset.UtcNow,
                    RunId: info.WorkflowRunId,
                    ActivityId: info.ActivityId,
                    Attempt: currentModelAttempt,
                    Model: actualModel,
                    GeneratedTokens: outcome.Usage?.CompletionTokens,
                    Succeeded: true,
                    Metadata: CreateMetadata(outcome),
                    Diagnostics: CreateDiagnostics(outcome, false),
                    MaximumAttempts: currentMaximumModelAttempts,
                    WillRetry: false));

            return Map(outcome) with
            {
                PlanningArtifacts = planningArtifacts,
                RepositoryContext = request.RepositoryContext
            };
        }
        catch (CodeGenerationActivityException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await PublishSafelyAsync(
                workflowId,
                new WorkflowProgress(
                    WorkflowProgressEventType.Canceled,
                    "Planning",
                    "Planning was canceled.",
                    DateTimeOffset.UtcNow,
                    RunId: info.WorkflowRunId,
                    ActivityId: info.ActivityId,
                    Attempt: transportAttempt,
                    Model: settings.PlannerModel,
                    Succeeded: false,
                    MaximumAttempts: maximumAttempts,
                    WillRetry: false));
            throw;
        }
        catch (Exception exception)
        {
            var transient = ActivityExceptionClassifier.IsTransient(exception);
            var willRetry = transient && transportAttempt < maximumAttempts;
            logger.LogError(
                exception,
                "Unexpected planning activity failure for workflow {WorkflowId}.",
                workflowId);
            await PublishSafelyAsync(
                workflowId,
                new WorkflowProgress(
                    WorkflowProgressEventType.Failed,
                    "Planning",
                    willRetry
                        ? $"{exception.Message} Planning will retry automatically."
                        : exception.Message,
                    DateTimeOffset.UtcNow,
                    RunId: info.WorkflowRunId,
                    ActivityId: info.ActivityId,
                    Attempt: transportAttempt,
                    Model: settings.PlannerModel,
                    Succeeded: false,
                    Metadata: UnexpectedFailureMetadata(exception),
                    Diagnostics: UnexpectedFailureDiagnostics(
                        "planning-activity-exception",
                        "Planning failed unexpectedly.",
                        exception),
                    MaximumAttempts: maximumAttempts,
                    WillRetry: willRetry));
            throw new CodeGenerationActivityException(
                exception.Message,
                exception,
                errorType: exception.GetType().Name,
                nonRetryable: !transient);
        }
    }

    private static IReadOnlyDictionary<string, string>
        UnexpectedFailureMetadata(Exception exception) =>
        new Dictionary<string, string>
        {
            ["exceptionType"] = exception.GetType().FullName ??
                exception.GetType().Name,
            ["failureKind"] = "UnhandledActivityException"
        };

    private static IReadOnlyList<WorkflowDiagnostic>
        UnexpectedFailureDiagnostics(
            string code,
            string message,
            Exception exception) =>
        [
            new(
                WorkflowDiagnosticSeverity.Error,
                code,
                message,
                [exception.Message])
        ];

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
                "Unable to publish planning progress for workflow {WorkflowId}.",
                workflowId);
        }
    }

    private static IReadOnlyDictionary<string, string> CreateMetadata(
        CodeGenerationPlanningOutcome outcome)
    {
        var metadata = new Dictionary<string, string>
        {
            ["failure"] = outcome.Failure.ToString(),
            ["jsonWasRepaired"] = outcome.JsonWasRepaired.ToString(),
            ["taskCount"] = outcome.Plan?.Tasks.Count.ToString() ?? "0",
            ["totalPoints"] = outcome.Plan?.Tasks
                .Sum(task => task.ComplexityPoints)
                .ToString() ?? "0"
        };
        if (!outcome.Succeeded)
            metadata["failureFingerprint"] = FailureFingerprint.Create(
                outcome.Failure.ToString(),
                outcome.Error);
        return metadata;
    }

    private static IReadOnlyList<WorkflowDiagnostic> CreateDiagnostics(
        CodeGenerationPlanningOutcome outcome,
        bool includeFailure)
    {
        var diagnostics = new List<WorkflowDiagnostic>();

        if (outcome.JsonWasRepaired)
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowDiagnosticSeverity.Warning,
                "plan-json-repaired",
                "The planning response required JSON repair.",
                outcome.JsonRepairAttempts
                    .Where(attempt =>
                        attempt.Status is not
                            LlmRepairStatus.Skipped and not
                            LlmRepairStatus.NotApplicable)
                    .Select(attempt => $"{attempt.Name}: {attempt.Status}")
                    .ToArray()));
        }

        if (includeFailure && !string.IsNullOrWhiteSpace(outcome.Error))
        {
            var evidence = FailureFingerprint.Evidence(
                outcome.Failure.ToString(),
                outcome.Error);
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowDiagnosticSeverity.Error,
                $"planning-{outcome.Failure.ToString().ToLowerInvariant()}",
                "The planning activity failed.",
                new[] { outcome.Error }.Concat(evidence).ToArray()));
        }

        return diagnostics;
    }

    private static CodeGenerationWorkflowResult Map(
        CodeGenerationPlanningOutcome outcome) =>
        new(
            Succeeded: outcome.Succeeded,
            Failure: outcome.Failure.ToString(),
            Error: outcome.Error,
            Model: outcome.Model,
            JsonWasRepaired: outcome.JsonWasRepaired,
            JsonRepairAttempts: outcome.JsonRepairAttempts,
            WrittenFiles: [],
            SkippedFiles: [],
            Usage: outcome.Usage is null
                ? null
                : new CodeGenerationUsage(
                    outcome.Usage.PromptTokens,
                    outcome.Usage.CompletionTokens,
                    outcome.Usage.TotalTokens,
                    outcome.Usage.PromptCacheHitTokens,
                    outcome.Usage.PromptCacheMissTokens),
            Diagnostics: outcome.Diagnostics is null
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
                    outcome.Diagnostics.ContentLength))
        {
            FinishReason = outcome.FinishReason,
            Plan = outcome.Plan
        };

    internal static string BuildPlanningRequest(
        CodeGenerationWorkflowRequest request,
        CodeGenerationWorkerOptions settings)
    {
        if (!settings.IncludeRepositoryContextInPrompts ||
            request.RepositoryContext is null ||
            string.IsNullOrWhiteSpace(request.RepositoryContext.Content))
            return request.Prompt;

        var context = SessionContextAssembler.Assemble(
            request.RepositoryContext,
            "code-generation-planning",
            settings.RepositoryContextMaximumPromptCharacters)!;

        return $"""
            {request.Prompt}

            {context.Content}
            """;
    }

    private async Task<IReadOnlyList<ArtifactReference>>
        WritePlanningArtifactsAsync(
            string workflowId,
            StagedPlanningArtifacts artifacts,
            CancellationToken cancellationToken)
    {
        var references = new List<ArtifactReference>();
        var domain = await artifactRepository.WriteAsync(
            new ArtifactWriteRequest<DomainDiscovery>(
                workflowId,
                "domain-discovery",
                1,
                "domain",
                ArtifactStatus.Validated,
                artifacts.Domain),
            cancellationToken);
        references.Add(domain.Reference);

        var topology = await artifactRepository.WriteAsync(
            new ArtifactWriteRequest<SolutionTopology>(
                workflowId,
                "solution-topology",
                1,
                "topology",
                ArtifactStatus.Validated,
                artifacts.Topology,
                [domain.Reference]),
            cancellationToken);
        references.Add(topology.Reference);

        var contractReferences = new Dictionary<string, ArtifactReference>(
            StringComparer.Ordinal);
        foreach (var catalog in artifacts.ContractCatalogs)
        {
            var context = artifacts.Topology.BoundedContexts.Single(item =>
                item.Name.Equals(
                    catalog.BoundedContextName,
                    StringComparison.Ordinal));
            var inputs = new List<ArtifactReference> { topology.Reference };
            inputs.AddRange(context.DependsOnContextNames
                .Where(contractReferences.ContainsKey)
                .Select(name => contractReferences[name]));
            var envelope = await artifactRepository.WriteAsync(
                new ArtifactWriteRequest<BoundedContextContractCatalog>(
                    workflowId,
                    "bounded-context-contracts",
                    1,
                    catalog.BoundedContextName,
                    ArtifactStatus.Validated,
                    catalog,
                    inputs),
                cancellationToken);
            contractReferences[catalog.BoundedContextName] =
                envelope.Reference;
            references.Add(envelope.Reference);
        }

        var componentReferences = new Dictionary<string, ArtifactReference>(
            StringComparer.Ordinal);
        foreach (var manifest in artifacts.ComponentManifests)
        {
            var context = artifacts.Topology.BoundedContexts.Single(item =>
                item.Name.Equals(
                    manifest.BoundedContextName,
                    StringComparison.Ordinal));
            var inputs = new List<ArtifactReference>
            {
                contractReferences[manifest.BoundedContextName]
            };
            inputs.AddRange(context.DependsOnContextNames
                .Where(componentReferences.ContainsKey)
                .Select(name => componentReferences[name]));
            var envelope = await artifactRepository.WriteAsync(
                new ArtifactWriteRequest<BoundedContextComponentManifest>(
                    workflowId,
                    "bounded-context-components",
                    1,
                    manifest.BoundedContextName,
                    ArtifactStatus.Validated,
                    manifest,
                    inputs),
                cancellationToken);
            componentReferences[manifest.BoundedContextName] =
                envelope.Reference;
            references.Add(envelope.Reference);
        }

        return references;
    }
}
