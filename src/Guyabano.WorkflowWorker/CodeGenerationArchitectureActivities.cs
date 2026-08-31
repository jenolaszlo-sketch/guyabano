using System.Text.Json;
using Microsoft.Extensions.Options;
using Penghou.Baize;
using Penghou.Cangjie;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.Prompting;
using Guyabano.Messaging;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationArchitectureActivities(
    IArchitectureReviewService reviewService,
    IArchitectureDecisionIntegrator decisionIntegrator,
    IArchitectureGapResolutionService resolutionService,
    IArchitecturePracticeProvider practiceProvider,
    IArtifactRepository artifactRepository,
    CangjieRevisionedConceptService cangjieConcepts,
    IContextStore contextStore,
    IWorkflowProgressPublisher progressPublisher,
    IOptions<CodeGenerationWorkerOptions> options,
    CodeGenerationWorkspaceResolver workspaceResolver,
    ILogger<CodeGenerationArchitectureActivities> logger)
{
    public async Task<ArchitectureReviewWorkflowResult> ReviewAsync(
        ArchitectureReviewWorkflowRequest request)
    {
        var settings = options.Value;
        var context = CodeGenerationActivityExecutionContext.Current;
        var info = context.Info;
        var workflowId = GetWorkflowId(info);
        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            workflowId,
            context.CancellationToken);
        var assembledContext = settings.IncludeRepositoryContextInPrompts
            ? SessionContextAssembler.Assemble(
                request.RepositoryContext,
                "architecture-review",
                settings.RepositoryContextMaximumPromptCharacters)
            : null;
        using var disclosureScope = SessionContextDisclosureScope.Push(
            assembledContext?.Content);
        using var correlationScope = LlmRequestCorrelationScope.Push(new(
            workspace.SessionId.ToString(),
            workflowId,
            info.ActivityId,
            CangjieSnapshotId: request.RepositoryContext?.SnapshotId,
            CangjieStrategy: request.RepositoryContext?.Strategy,
            CangjieStrategyVersion: request.RepositoryContext?.StrategyVersion,
            CangjiePurpose: "architecture-review",
            HetuIndexRunId: request.RepositoryContext?.Revision.IndexRunId,
            HetuIndexIdentity: request.RepositoryContext?.Revision.WorkspaceRevision));
        var transportAttempt = info.Attempt;
        const int maximumAttempts =
            CodeGenerationWorkflowConstants.MaximumArchitectureTransportAttempts;
        var stage = $"Architecture review {request.ReviewPass} of {CodeGenerationWorkflowConstants.MaximumArchitectureReviewPasses}";

        await PublishAsync(workflowId, new(
            WorkflowProgressEventType.Started,
            stage,
            $"Independent architecture review started with {settings.ArchitectureReviewModel}.",
            DateTimeOffset.UtcNow,
            RunId: info.WorkflowRunId,
            ActivityId: info.ActivityId,
            Attempt: transportAttempt,
            Model: settings.ArchitectureReviewModel,
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
            ArchitectureReviewOutcome outcome;
            while (true)
            {
                outcome = await reviewService.ReviewAsync(
                    request.Plan,
                    request.ReviewPass,
                    settings.ArchitectureReviewModel,
                    settings.ArchitectureReviewMaxTokens,
                    request.PreviousReview,
                    retryState.PreviousFailure,
                    context.CancellationToken);
                if (outcome.Succeeded && outcome.Review is not null)
                    break;

                var failureKind = PlanningModelRetryPolicy.Classify(
                    outcome.Failure);
                var modelAttempt = retryState.Attempt(failureKind);
                var maximumModelAttempts =
                    PlanningModelRetryPolicy.MaximumAttempts(failureKind);
                currentModelAttempt = modelAttempt;
                currentMaximumModelAttempts = maximumModelAttempts;
                var willRetryModel = modelAttempt < maximumModelAttempts;
                var failedModel = outcome.Diagnostics?.ActualModel ?? outcome.Model;
                await PublishAsync(workflowId, new(
                    WorkflowProgressEventType.Failed,
                    stage,
                    willRetryModel
                        ? $"{outcome.Error} A {failureKind.ToString().ToLowerInvariant()} model retry will start automatically."
                        : outcome.Error ?? "Architecture review failed.",
                    DateTimeOffset.UtcNow,
                    RunId: info.WorkflowRunId,
                    ActivityId: info.ActivityId,
                    Attempt: modelAttempt,
                    Model: failedModel,
                    GeneratedTokens: outcome.Usage?.CompletionTokens,
                    Succeeded: false,
                    Metadata: new Dictionary<string, string>
                    {
                        ["retryKind"] = failureKind.ToString(),
                        ["transportAttempt"] = transportAttempt.ToString(),
                        ["reviewPass"] = request.ReviewPass.ToString()
                    },
                    Diagnostics: RepairDiagnostics(
                        outcome.JsonWasRepaired,
                        outcome.JsonRepairAttempts,
                        outcome.Error),
                    MaximumAttempts: maximumModelAttempts,
                    WillRetry: willRetryModel));
                if (!willRetryModel)
                    return Map(request.ReviewPass, outcome, artifact: null);

                retryState = retryState.Record(
                    failureKind,
                    outcome.Error ?? outcome.Failure.ToString());
                context.Heartbeat(retryState);
                currentModelAttempt = modelAttempt + 1;
                await PublishAsync(workflowId, new(
                    WorkflowProgressEventType.Started,
                    stage,
                    $"Architecture review {failureKind.ToString().ToLowerInvariant()} retry {currentModelAttempt} of {maximumModelAttempts} started with {settings.ArchitectureReviewModel}.",
                    DateTimeOffset.UtcNow,
                    RunId: info.WorkflowRunId,
                    ActivityId: info.ActivityId,
                    Attempt: currentModelAttempt,
                    Model: settings.ArchitectureReviewModel,
                    Metadata: new Dictionary<string, string>
                    {
                        ["retryKind"] = failureKind.ToString(),
                        ["transportAttempt"] = transportAttempt.ToString(),
                        ["reviewPass"] = request.ReviewPass.ToString()
                    },
                    MaximumAttempts: maximumModelAttempts,
                    WillRetry: false));
            }
            var actualModel = outcome.Diagnostics?.ActualModel ?? outcome.Model;
            ArtifactReference? artifact = null;
            var accepted = outcome.Succeeded && outcome.Review is not null &&
                (outcome.Review.Findings.Count == 0 ||
                 request.ReviewPass ==
                     CodeGenerationWorkflowConstants.MaximumArchitectureReviewPasses &&
                 outcome.Review.Approved);
            if (accepted)
            {
                var envelope = await artifactRepository.WriteAsync(
                    new ArtifactWriteRequest<ArchitecturePlanArtifactPayload>(
                        WorkflowId: workflowId,
                        Kind: "architecture-plan",
                        SchemaVersion: 1,
                        StageKey: $"architecture-v{request.ArchitectureVersion}",
                        Status: ArtifactStatus.Validated,
                        Payload: new(
                            request.ArchitectureVersion,
                            request.Plan,
                            outcome.Review!,
                            request.RepositoryContext?.Revision.WorkspaceRevision,
                            request.RepositoryContext?.Revision.IndexRunId),
                        Inputs: request.PreviousArchitectureArtifact is null
                            ? request.PlanningArtifacts ?? []
                            : new[] { request.PreviousArchitectureArtifact }
                                .Concat(request.PlanningArtifacts ?? [])
                                .DistinctBy(
                                    item => item.ArtifactId,
                                    StringComparer.Ordinal)
                                .ToArray()),
                    context.CancellationToken);
                artifact = envelope.Reference;
            }
            try
            {
                var cangjieStepContext = CodeGenerationZhinuStepScope.Current;
                var cangjieStepKey = cangjieStepContext?.StepKey ?? info.ActivityId;
                var cangjieStepRevision = cangjieStepContext?.Revision ?? 1;
                var repositoryId = settings.RepositoryContextEnabled ? settings.RepositoryId : null;
                var evidenceKey = $"architecture-review:{request.ArchitectureVersion}:{request.ReviewPass}";
                var evidenceContent = JsonSerializer.Serialize(outcome.Review, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var evidenceItem = await cangjieConcepts.StoreEvidenceAsync(
                    sessionId: workspace.SessionId.ToString(),
                    evidenceKey: evidenceKey,
                    content: evidenceContent,
                    workflowRunId: workflowId,
                    stepKey: cangjieStepKey,
                    stepRevision: cangjieStepRevision,
                    repositoryId: repositoryId,
                    cancellationToken: context.CancellationToken);
                if (accepted)
                {
                    foreach (var decision in request.Plan.Decisions)
                    {
                        await cangjieConcepts.StoreDecisionAsync(
                            sessionId: workspace.SessionId.ToString(),
                            decision: decision,
                            workflowRunId: workflowId,
                            stepKey: cangjieStepKey,
                            stepRevision: cangjieStepRevision,
                            repositoryId: repositoryId,
                            derivedFromIds: [evidenceItem.Id],
                            cancellationToken: context.CancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to store Cangjie concepts for workflow {WorkflowId}", workflowId);
            }
            {
                await PublishAsync(workflowId, new(
                    WorkflowProgressEventType.Completed,
                    stage,
                    outcome.Review!.Approved
                        ? outcome.Review.Findings.Count == 0
                            ? "Architecture approved with no findings."
                            : $"Architecture has no blocking findings; {outcome.Review.Findings.Count} actionable warning(s) were recorded."
                        : $"Architecture review found {outcome.Review.Findings.Count} issue(s), including {outcome.Review.Findings.Count(item => item.Severity == ArchitectureReviewSeverity.Blocking)} blocking issue(s).",
                    DateTimeOffset.UtcNow,
                    RunId: info.WorkflowRunId,
                    ActivityId: info.ActivityId,
                    Attempt: currentModelAttempt,
                    Model: actualModel,
                    GeneratedTokens: outcome.Usage?.CompletionTokens,
                    Succeeded: true,
                    Metadata: new Dictionary<string, string>
                    {
                        ["reviewPass"] = request.ReviewPass.ToString(),
                        ["approved"] = outcome.Review.Approved.ToString(),
                        ["findingCount"] = outcome.Review.Findings.Count.ToString(),
                        ["blockingCount"] = outcome.Review.Findings
                            .Count(item => item.Severity == ArchitectureReviewSeverity.Blocking)
                            .ToString(),
                        ["architectureVersion"] =
                            request.ArchitectureVersion.ToString(),
                        ["architectureArtifactId"] =
                            artifact?.ArtifactId ?? string.Empty
                    },
                    Diagnostics: FindingDiagnostics(outcome),
                    MaximumAttempts: currentMaximumModelAttempts,
                    WillRetry: false));
            }

            return Map(request.ReviewPass, outcome, artifact);
        }
        catch (CodeGenerationActivityException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await PublishUnhandledAsync(
                workflowId,
                info,
                stage,
                settings.ArchitectureReviewModel,
                transportAttempt,
                maximumAttempts,
                new OperationCanceledException(
                    "Architecture review was canceled."),
                WorkflowProgressEventType.Canceled,
                willRetry: false);
            throw;
        }
        catch (Exception exception)
        {
            var transient = ActivityExceptionClassifier.IsTransient(exception);
            var willRetry = transient && transportAttempt < maximumAttempts;
            await PublishUnhandledAsync(
                workflowId,
                info,
                stage,
                settings.ArchitectureReviewModel,
                transportAttempt,
                maximumAttempts,
                exception,
                WorkflowProgressEventType.Failed,
                willRetry);
            throw new CodeGenerationActivityException(
                exception.Message,
                exception,
                errorType: exception.GetType().Name,
                nonRetryable: !transient);
        }
    }

    public async Task<ArchitectureGapResolutionWorkflowResult> ResolveGapAsync(
        ArchitectureGapResolutionWorkflowRequest request)
    {
        var settings = options.Value;
        var context = CodeGenerationActivityExecutionContext.Current;
        var info = context.Info;
        var workflowId = GetWorkflowId(info);
        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            workflowId,
            context.CancellationToken);
        var assembledContext = settings.IncludeRepositoryContextInPrompts
            ? SessionContextAssembler.Assemble(
                request.RepositoryContext,
                "architecture-gap-resolution",
                settings.RepositoryContextMaximumPromptCharacters)
            : null;
        using var disclosureScope = SessionContextDisclosureScope.Push(
            assembledContext?.Content);
        ContextSnapshot? gapSnapshot = null;
        if (request.RepositoryContext is not null)
        {
            try
            {
                gapSnapshot = await CangjieSnapshotHelper.DeriveSnapshotAsync(
                    contextStore,
                    request.RepositoryContext.SnapshotId,
                    workspace.SessionId.ToString(),
                    workflowId,
                    info.ActivityId,
                    CodeGenerationZhinuStepScope.Current?.Revision ?? 1,
                    queryIdentity: $"guyabano:{workflowId}:architecture-gap:{request.Finding.Id}",
                    strategy: "architecture-gap-resolution",
                    strategyVersion: "2",
                    purpose: "architecture-gap-resolution",
                    workspaceRevision: request.RepositoryContext.Revision.WorkspaceRevision,
                    hetuIndexRunId: request.RepositoryContext.Revision.IndexRunId,
                    hetuIndexIdentity: request.RepositoryContext.Revision.WorkspaceRevision,
                    cancellationToken: context.CancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to derive the Cangjie snapshot for gap {GapId} workflow {WorkflowId}", request.Finding.Id, workflowId);
            }
        }

        using var correlationScope = gapSnapshot is not null
            ? LlmRequestCorrelationScope.Push(new(
                workspace.SessionId.ToString(),
                workflowId,
                info.ActivityId,
                CangjieSnapshotId: gapSnapshot.Id,
                CangjieStrategy: gapSnapshot.Strategy,
                CangjieStrategyVersion: gapSnapshot.StrategyVersion,
                CangjieQueryIdentity: gapSnapshot.QueryIdentity,
                CangjiePurpose: "architecture-gap-resolution",
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
                CangjiePurpose: "architecture-gap-resolution",
                HetuIndexRunId: request.RepositoryContext.Revision.IndexRunId,
                HetuIndexIdentity: request.RepositoryContext.Revision.WorkspaceRevision,
                WorkspaceRevision: request.RepositoryContext.Revision.WorkspaceRevision,
                WorkflowStepRevision: CodeGenerationZhinuStepScope.Current?.Revision))
            : LlmRequestCorrelationScope.Push(new(
                workspace.SessionId.ToString(),
                workflowId,
                info.ActivityId));
        var stage = $"Resolve architecture gap {request.Finding.Id}";
        const int maximumAttempts =
            CodeGenerationWorkflowConstants.MaximumArchitectureModelOutputAttempts;

        ArchitectureGapResolutionOutcome? outcome = null;
        var practices = MergePractices(
            practiceProvider.GetPractices(),
            request.ArchitecturePractices ?? []);
        string? previousFailure = null;
        var successfulAttempt = 1;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            await PublishAsync(workflowId, new(
                WorkflowProgressEventType.Started,
                stage,
                attempt == 1
                    ? request.Finding.Summary
                    : "Retrying the focused architecture gap resolution.",
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: attempt,
                Model: settings.ArchitectureReviewModel,
                MaximumAttempts: maximumAttempts));

            outcome = await resolutionService.ResolveAsync(
                request.Plan,
                request.Finding,
                practices,
                request.ArchitectureVersion,
                settings.ArchitectureReviewModel,
                settings.ArchitectureReviewMaxTokens,
                previousFailure,
                context.CancellationToken);
            if (outcome.Succeeded && outcome.Resolution is not null)
            {
                successfulAttempt = attempt;
                break;
            }

            previousFailure = outcome.Error;
            var willRetry = attempt < maximumAttempts;
            await PublishAsync(workflowId, new(
                WorkflowProgressEventType.Failed,
                stage,
                willRetry
                    ? $"{outcome.Error} The focused resolver will correct its output."
                    : outcome.Error ?? "Gap resolution failed.",
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: attempt,
                Model: outcome.Diagnostics?.ActualModel ?? outcome.Model,
                GeneratedTokens: outcome.Usage?.CompletionTokens,
                Succeeded: false,
                Diagnostics: RepairDiagnostics(
                    outcome.JsonWasRepaired,
                    outcome.JsonRepairAttempts,
                    outcome.Error),
                MaximumAttempts: maximumAttempts,
                WillRetry: willRetry));
            if (!willRetry)
                return Map(outcome, artifact: null);
        }

        var resolution = outcome!.Resolution!;
        var inputs = new[] { request.ArchitectureArtifact }
            .Where(item => item is not null)
            .Select(item => item!)
            .Concat(request.PlanningArtifacts)
            .DistinctBy(item => item.ArtifactId, StringComparer.Ordinal)
            .ToArray();
        var envelope = await artifactRepository.WriteAsync(
            new ArtifactWriteRequest<ArchitectureGapResolution>(
                workflowId,
                "architecture-gap-resolution",
                1,
                $"v{request.ArchitectureVersion}-{request.Finding.Id}",
                ArtifactStatus.Validated,
                resolution,
                inputs),
            context.CancellationToken);

        await PublishAsync(workflowId, new(
            WorkflowProgressEventType.Completed,
            stage,
            resolution.RequiresUserInput
                ? "The gap requires a product decision from the user."
                : $"Resolved: {resolution.Decision}",
            DateTimeOffset.UtcNow,
            RunId: info.WorkflowRunId,
            ActivityId: info.ActivityId,
            Attempt: successfulAttempt,
            Model: outcome.Diagnostics?.ActualModel ?? outcome.Model,
            GeneratedTokens: outcome.Usage?.CompletionTokens,
            Succeeded: true,
            Metadata: new Dictionary<string, string>
            {
                ["findingId"] = request.Finding.Id,
                ["resolutionKind"] = resolution.ResolutionKind,
                ["practiceId"] = resolution.AppliedPractice.Id,
                ["decisionId"] = resolution.DecisionRecord.Id,
                ["reusedPractice"] =
                    resolution.ReusedExistingPractice.ToString(),
                ["requiresUserInput"] = resolution.RequiresUserInput.ToString(),
                ["artifactId"] = envelope.Reference.ArtifactId
            },
            Diagnostics: RepairDiagnostics(
                outcome.JsonWasRepaired,
                outcome.JsonRepairAttempts,
                error: null),
            MaximumAttempts: maximumAttempts,
            WillRetry: false));
        return Map(outcome, envelope.Reference);
    }

    private static IReadOnlyList<ArchitecturePractice> MergePractices(
        IReadOnlyList<ArchitecturePractice> established,
        IReadOnlyList<ArchitecturePractice> project)
    {
        var result = established.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        foreach (var practice in project)
        {
            if (result.TryGetValue(practice.Id, out var existing) &&
                (!existing.Guidance.Equals(
                     practice.Guidance,
                     StringComparison.Ordinal) ||
                 !existing.Applicability.Equals(
                     practice.Applicability,
                     StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    $"Architecture practice '{practice.Id}' has conflicting definitions.");
            result[practice.Id] = practice;
        }
        return result.Values.OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ArchitectureDecisionIntegrationWorkflowResult> IntegrateAsync(
        ArchitectureDecisionIntegrationWorkflowRequest request)
    {
        var settings = options.Value;
        var context = CodeGenerationActivityExecutionContext.Current;
        var info = context.Info;
        var workflowId = GetWorkflowId(info);
        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            workflowId,
            context.CancellationToken);
        var assembledContext = settings.IncludeRepositoryContextInPrompts
            ? SessionContextAssembler.Assemble(
                request.RepositoryContext,
                "architecture-integration",
                settings.RepositoryContextMaximumPromptCharacters)
            : null;
        using var disclosureScope = SessionContextDisclosureScope.Push(
            assembledContext?.Content);
        var nextVersion = request.ArchitectureVersion + 1;
        ContextSnapshot? integrationSnapshot = null;
        if (request.RepositoryContext is not null)
        {
            try
            {
                integrationSnapshot = await CangjieSnapshotHelper.DeriveSnapshotAsync(
                    contextStore,
                    request.RepositoryContext.SnapshotId,
                    workspace.SessionId.ToString(),
                    workflowId,
                    info.ActivityId,
                    CodeGenerationZhinuStepScope.Current?.Revision ?? 1,
                    queryIdentity: $"guyabano:{workflowId}:architecture-integration:{nextVersion}",
                    strategy: "architecture-integration",
                    strategyVersion: "2",
                    purpose: "architecture-integration",
                    workspaceRevision: request.RepositoryContext.Revision.WorkspaceRevision,
                    hetuIndexRunId: request.RepositoryContext.Revision.IndexRunId,
                    hetuIndexIdentity: request.RepositoryContext.Revision.WorkspaceRevision,
                    cancellationToken: context.CancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to derive the Cangjie snapshot for architecture integration {WorkflowId} step {StepKey}", workflowId, info.ActivityId);
            }
        }

        using var correlationScope = integrationSnapshot is not null
            ? LlmRequestCorrelationScope.Push(new(
                workspace.SessionId.ToString(),
                workflowId,
                info.ActivityId,
                CangjieSnapshotId: integrationSnapshot.Id,
                CangjieStrategy: integrationSnapshot.Strategy,
                CangjieStrategyVersion: integrationSnapshot.StrategyVersion,
                CangjieQueryIdentity: integrationSnapshot.QueryIdentity,
                CangjiePurpose: "architecture-integration",
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
                CangjiePurpose: "architecture-integration",
                HetuIndexRunId: request.RepositoryContext.Revision.IndexRunId,
                HetuIndexIdentity: request.RepositoryContext.Revision.WorkspaceRevision,
                WorkspaceRevision: request.RepositoryContext.Revision.WorkspaceRevision,
                WorkflowStepRevision: CodeGenerationZhinuStepScope.Current?.Revision))
            : LlmRequestCorrelationScope.Push(new(
                workspace.SessionId.ToString(),
                workflowId,
                info.ActivityId));
        var transportAttempt = info.Attempt;
        const int maximumAttempts =
            CodeGenerationWorkflowConstants.MaximumArchitectureTransportAttempts;
        var stage = $"Architecture decision integration v{nextVersion}";

        await PublishAsync(workflowId, new(
            WorkflowProgressEventType.Started,
            stage,
            $"Architecture decision integration started with {settings.ArchitectureIntegratorModel}.",
            DateTimeOffset.UtcNow,
            RunId: info.WorkflowRunId,
            ActivityId: info.ActivityId,
            Attempt: transportAttempt,
            Model: settings.ArchitectureIntegratorModel,
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
            ArchitectureDecisionIntegrationOutcome outcome;
            while (true)
            {
                outcome = await decisionIntegrator.IntegrateAsync(
                    request.Plan,
                    request.ResolvedReview,
                    request.ResolvedDecisions,
                    settings.ArchitectureIntegratorModel,
                    settings.ArchitectureIntegratorMaxTokens,
                    retryState.PreviousFailure,
                    context.CancellationToken);
                if (outcome.Succeeded && outcome.IntegratedPlan is not null)
                    break;

                var failureKind = PlanningModelRetryPolicy.Classify(
                    outcome.Failure);
                var modelAttempt = retryState.Attempt(failureKind);
                var maximumModelAttempts =
                    PlanningModelRetryPolicy.MaximumAttempts(failureKind);
                currentModelAttempt = modelAttempt;
                currentMaximumModelAttempts = maximumModelAttempts;
                var willRetryModel = modelAttempt < maximumModelAttempts;
                var failedModel = outcome.Diagnostics?.ActualModel ?? outcome.Model;
                await PublishAsync(workflowId, new(
                    WorkflowProgressEventType.Failed,
                    stage,
                    willRetryModel
                        ? $"{outcome.Error} A {failureKind.ToString().ToLowerInvariant()} model retry will start automatically."
                        : outcome.Error ?? "Architecture decision integration failed.",
                    DateTimeOffset.UtcNow,
                    RunId: info.WorkflowRunId,
                    ActivityId: info.ActivityId,
                    Attempt: modelAttempt,
                    Model: failedModel,
                    GeneratedTokens: outcome.Usage?.CompletionTokens,
                    Succeeded: false,
                    Metadata: new Dictionary<string, string>
                    {
                        ["retryKind"] = failureKind.ToString(),
                        ["transportAttempt"] = transportAttempt.ToString(),
                        ["architectureVersion"] = nextVersion.ToString()
                    },
                    Diagnostics: RepairDiagnostics(
                        outcome.JsonWasRepaired,
                        outcome.JsonRepairAttempts,
                        outcome.Error),
                    MaximumAttempts: maximumModelAttempts,
                    WillRetry: willRetryModel));
                if (!willRetryModel)
                    return Map(nextVersion, outcome);

                retryState = retryState.Record(
                    failureKind,
                    outcome.Error ?? outcome.Failure.ToString());
                context.Heartbeat(retryState);
                currentModelAttempt = modelAttempt + 1;
                await PublishAsync(workflowId, new(
                    WorkflowProgressEventType.Started,
                    stage,
                    $"Architecture decision integration {failureKind.ToString().ToLowerInvariant()} retry {currentModelAttempt} of {maximumModelAttempts} started with {settings.ArchitectureIntegratorModel}.",
                    DateTimeOffset.UtcNow,
                    RunId: info.WorkflowRunId,
                    ActivityId: info.ActivityId,
                    Attempt: currentModelAttempt,
                    Model: settings.ArchitectureIntegratorModel,
                    Metadata: new Dictionary<string, string>
                    {
                        ["retryKind"] = failureKind.ToString(),
                        ["transportAttempt"] = transportAttempt.ToString(),
                        ["architectureVersion"] = nextVersion.ToString()
                    },
                    MaximumAttempts: maximumModelAttempts,
                    WillRetry: false));
            }
            var actualModel = outcome.Diagnostics?.ActualModel ?? outcome.Model;
            ArtifactReference? artifact = null;
            if (outcome.Patch is not null && outcome.IntegratedPlan is not null)
            {
                var inputs = new[] { request.PreviousArchitectureArtifact }
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .Concat(request.ResolutionArtifacts ?? [])
                    .DistinctBy(item => item.ArtifactId, StringComparer.Ordinal)
                    .ToArray();
                var envelope = await artifactRepository.WriteAsync(
                    new ArtifactWriteRequest<
                        ArchitectureDecisionIntegrationArtifactPayload>(
                        WorkflowId: workflowId,
                        Kind: "architecture-decision-patch",
                        SchemaVersion: 1,
                        StageKey: $"architecture-integration-v{nextVersion}",
                        Status: ArtifactStatus.Validated,
                        Payload: new(
                            nextVersion,
                            outcome.Patch,
                            outcome.IntegratedPlan),
                        Inputs: inputs),
                    context.CancellationToken);
                artifact = envelope.Reference;
            }

            await PublishAsync(workflowId, new(
                outcome.Succeeded
                    ? WorkflowProgressEventType.Completed
                    : WorkflowProgressEventType.Failed,
                stage,
                outcome.Succeeded
                    ? $"Architecture decision patch integrated as version {nextVersion}; {request.ResolvedReview.Findings.Count} resolved finding(s) were applied."
                    : outcome.Error ?? "Architecture decision integration failed.",
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: currentModelAttempt,
                Model: actualModel,
                GeneratedTokens: outcome.Usage?.CompletionTokens,
                Succeeded: outcome.Succeeded,
                Metadata: new Dictionary<string, string>
                {
                    ["architectureVersion"] = nextVersion.ToString(),
                    ["findingCount"] = request.ResolvedReview.Findings.Count.ToString(),
                    ["architecturePatchArtifactId"] =
                        artifact?.ArtifactId ?? string.Empty
                },
                Diagnostics: RepairDiagnostics(
                    outcome.JsonWasRepaired,
                    outcome.JsonRepairAttempts,
                    outcome.Error),
                MaximumAttempts: currentMaximumModelAttempts,
                WillRetry: false));
            return Map(nextVersion, outcome, artifact);
        }
        catch (CodeGenerationActivityException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await PublishUnhandledAsync(
                workflowId,
                info,
                stage,
                settings.ArchitectureIntegratorModel,
                transportAttempt,
                maximumAttempts,
                new OperationCanceledException(
                    "Architecture decision integration was canceled."),
                WorkflowProgressEventType.Canceled,
                willRetry: false);
            throw;
        }
        catch (Exception exception)
        {
            var transient = ActivityExceptionClassifier.IsTransient(exception);
            var willRetry = transient && transportAttempt < maximumAttempts;
            await PublishUnhandledAsync(
                workflowId,
                info,
                stage,
                settings.ArchitectureIntegratorModel,
                transportAttempt,
                maximumAttempts,
                exception,
                WorkflowProgressEventType.Failed,
                willRetry);
            throw new CodeGenerationActivityException(
                exception.Message,
                exception,
                errorType: exception.GetType().Name,
                nonRetryable: !transient);
        }
    }

    private Task PublishUnhandledAsync(
        string workflowId,
        CodeGenerationActivityInfo info,
        string stage,
        string model,
        int attempt,
        int maximumAttempts,
        Exception exception,
        WorkflowProgressEventType eventType,
        bool willRetry) =>
        PublishAsync(workflowId, new(
            eventType,
            stage,
            willRetry
                ? $"{exception.Message} The activity failed unexpectedly and will retry automatically."
                : exception.Message,
            DateTimeOffset.UtcNow,
            RunId: info.WorkflowRunId,
            ActivityId: info.ActivityId,
            Attempt: attempt,
            Model: model,
            Succeeded: false,
            Metadata: new Dictionary<string, string>
            {
                ["exceptionType"] = exception.GetType().FullName ??
                    exception.GetType().Name,
                ["failureKind"] = "UnhandledActivityException"
            },
            Diagnostics:
            [
                new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    "architecture-activity-exception",
                    "Architecture activity failed unexpectedly.",
                    [exception.Message])
            ],
            MaximumAttempts: maximumAttempts,
            WillRetry: willRetry));

    private async Task PublishAsync(string workflowId, WorkflowProgress progress)
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
            logger.LogWarning(exception,
                "Unable to publish architecture progress for {WorkflowId}.",
                workflowId);
        }
    }

    private static string GetWorkflowId(CodeGenerationActivityInfo info) =>
        info.WorkflowId;

    private static IReadOnlyList<WorkflowDiagnostic> FindingDiagnostics(
        ArchitectureReviewOutcome outcome)
    {
        var result = RepairDiagnostics(
            outcome.JsonWasRepaired,
            outcome.JsonRepairAttempts,
            error: null).ToList();
        result.AddRange(outcome.Review!.Findings.Select(finding => new WorkflowDiagnostic(
            finding.Severity == ArchitectureReviewSeverity.Blocking
                ? WorkflowDiagnosticSeverity.Error
                : WorkflowDiagnosticSeverity.Warning,
            finding.Id,
            finding.Summary,
            finding.Evidence
                .Concat([$"Suggested resolution: {finding.SuggestedResolution}"])
                .ToArray())));
        return result;
    }

    private static IReadOnlyList<WorkflowDiagnostic> RepairDiagnostics(
        bool repaired,
        IReadOnlyList<LlmRepairAttempt> attempts,
        string? error)
    {
        var result = new List<WorkflowDiagnostic>();
        if (repaired)
            result.Add(new(
                WorkflowDiagnosticSeverity.Warning,
                "architecture-json-repaired",
                "The architecture response required JSON repair.",
                attempts.Where(item => item.Status is not
                        LlmRepairStatus.Skipped and not
                        LlmRepairStatus.NotApplicable)
                    .Select(item => $"{item.Name}: {item.Status}").ToArray()));
        if (!string.IsNullOrWhiteSpace(error))
            result.Add(new(
                WorkflowDiagnosticSeverity.Error,
                "architecture-processing-failed",
                "Architecture processing failed.",
                [error]));
        return result;
    }

    private static ArchitectureReviewWorkflowResult Map(
        int pass,
        ArchitectureReviewOutcome outcome,
        ArtifactReference? artifact) =>
        new(pass, outcome.Succeeded, outcome.Failure.ToString(), outcome.Error,
            outcome.Model, outcome.Review, outcome.JsonWasRepaired,
            outcome.JsonRepairAttempts, MapUsage(outcome.Usage),
            MapDiagnostics(outcome.Diagnostics), outcome.FinishReason,
            artifact);

    private static ArchitectureDecisionIntegrationWorkflowResult Map(
        int version,
        ArchitectureDecisionIntegrationOutcome outcome,
        ArtifactReference? artifact = null) =>
        new(version, outcome.Succeeded, outcome.Failure.ToString(), outcome.Error,
            outcome.Model, outcome.Patch, outcome.IntegratedPlan,
            outcome.JsonWasRepaired, outcome.JsonRepairAttempts,
            MapUsage(outcome.Usage), MapDiagnostics(outcome.Diagnostics),
            outcome.FinishReason, artifact);

    private static ArchitectureGapResolutionWorkflowResult Map(
        ArchitectureGapResolutionOutcome outcome,
        ArtifactReference? artifact) =>
        new(
            outcome.Succeeded,
            outcome.Failure.ToString(),
            outcome.Error,
            outcome.Model,
            outcome.Resolution,
            outcome.JsonWasRepaired,
            outcome.JsonRepairAttempts,
            MapUsage(outcome.Usage),
            MapDiagnostics(outcome.Diagnostics),
            outcome.FinishReason,
            artifact);

    private static CodeGenerationUsage? MapUsage(Penghou.Baize.LlmUsage? usage) =>
        usage is null ? null : new(usage.PromptTokens, usage.CompletionTokens,
            usage.TotalTokens, usage.PromptCacheHitTokens,
            usage.PromptCacheMissTokens);

    private static CodeGenerationDiagnostics? MapDiagnostics(
        Penghou.Baize.LlmProviderDiagnostics? value) =>
        value is null ? null : new(value.Provider, value.ActualModel, value.Api,
            value.Done, value.DoneReason, value.TotalDurationMilliseconds,
            value.LoadDurationMilliseconds,
            value.PromptEvaluationDurationMilliseconds,
            value.GenerationDurationMilliseconds,
            value.GenerationTokensPerSecond, value.NativeToolCallCount,
            value.ContentLength);
}
