using Penghou.Baize;
using Penghou.Zhinu;
using Guyabano.CodeGeneration.Planning;

namespace Guyabano.CodeGeneration.Workflows;

public sealed class CodeGenerationWorkflow
    : IWorkflow<CodeGenerationWorkflowRequest, CodeGenerationWorkflowResult>
{
    public async Task<CodeGenerationWorkflowResult> RunAsync(
        WorkflowContext workflow,
        CodeGenerationWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContinuationMode != CodeGenerationContinuationMode.None)
            return await ContinueAsync(
                request,
                workflow,
                cancellationToken);

        request = await PrepareRepositoryContextAsync(
            workflow,
            request,
            cancellationToken);

        var planningResult = await workflow.StepAsync<
            CodeGenerationWorkflowRequest,
            CodeGenerationWorkflowResult>(
                "planning",
                CodeGenerationWorkflowConstants.PlanStep,
                request,
                new StepOptions
                {
                    ExecutionTimeout = TimeSpan.FromMinutes(15),
                    Retry = new RetryPolicy
                    {
                        InitialDelay = TimeSpan.FromSeconds(10),
                        BackoffCoefficient = 2,
                        MaximumDelay = TimeSpan.FromSeconds(30),
                        MaxAttempts =
                            CodeGenerationWorkflowConstants
                                .MaximumPlanningTransportAttempts
                    }
                },
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (!planningResult.Succeeded || planningResult.Plan is null)
            return planningResult;

        var architectureResult = planningResult;
        var architecturePlan = planningResult.Plan;
        var architectureReviews = new List<ArchitectureReviewWorkflowResult>();
        var architectureIntegrations =
            new List<ArchitectureDecisionIntegrationWorkflowResult>();
        var architectureApproved = false;

        for (var pass = 1;
             pass <= CodeGenerationWorkflowConstants.MaximumArchitectureReviewPasses;
             pass++)
        {
            var review = await workflow.StepAsync<
                ArchitectureReviewWorkflowRequest,
                ArchitectureReviewWorkflowResult>(
                $"architecture-review/{architectureResult.ArchitectureVersion}/{pass}",
                CodeGenerationWorkflowConstants.ReviewArchitectureStep,
                new ArchitectureReviewWorkflowRequest(
                    architecturePlan,
                    pass,
                    architectureReviews.LastOrDefault()?.Review,
                    architectureResult.ArchitectureVersion,
                    architectureResult.ArchitectureArtifact,
                    ArchitectureInputs(architectureResult)),
                ArchitectureActivityOptions(),
                cancellationToken);
            architectureReviews.Add(review);
            architectureResult = MergeArchitectureReview(
                architectureResult,
                architectureReviews,
                architectureIntegrations,
                review);

            if (!review.Succeeded || review.Review is null)
                return architectureResult;

            if (CanAcceptArchitecture(review.Review, pass))
            {
                architectureApproved = true;
                break;
            }

            if (pass ==
                CodeGenerationWorkflowConstants.MaximumArchitectureReviewPasses)
            {
                return architectureResult with
                {
                    Succeeded = false,
                    Failure = "ArchitectureReviewFailed",
                    Error = "Architecture still has blocking findings after the maximum review passes."
                };
            }

            var sequence = await ResolveArchitectureFindingsSequentiallyAsync(
                workflow,
                architecturePlan,
                review.Review,
                architectureResult,
                architectureReviews,
                architectureIntegrations,
                cancellationToken);
            architectureResult = sequence.Result;
            if (!sequence.Completed)
                return architectureResult;
            architecturePlan = sequence.Plan;
        }

        if (!architectureApproved)
            return architectureResult;

        planningResult = architectureResult with
        {
            Plan = architecturePlan,
            Succeeded = true,
            Failure = "None",
            Error = null
        };

        var decompositionResults =
            new List<CodeGenerationDecompositionWorkflowResult>();
        var decompositionArchitectureIntegrationBudget =
            new DecompositionArchitectureIntegrationBudget(
                CodeGenerationWorkflowConstants
                    .MaximumDecompositionArchitectureIntegrations);
        IReadOnlyList<GenerationTaskPlan> architectureTasks;
        while (true)
        {
            architectureTasks = OrderCodeGenerationTasks(planningResult.Plan!);
            CodeGenerationDecompositionWorkflowResult? architectureGap = null;
            while (true)
            {
                var completedTaskIds = planningResult.Plan.Tasks
                    .Where(item => item.ExecutionKind ==
                        PlanTaskExecutionKind.Scaffolding)
                    .Select(item => item.Id)
                    .Concat(decompositionResults
                        .Where(item => item.Succeeded)
                        .Select(item => item.ParentTaskId))
                    .ToHashSet(StringComparer.Ordinal);
                var pendingParents = architectureTasks
                    .Where(parent => !completedTaskIds.Contains(parent.Id))
                    .ToArray();
                if (pendingParents.Length == 0)
                    break;

                var readyParents = GetReadyCodeGenerationTasks(
                    planningResult.Plan,
                    completedTaskIds);
                if (readyParents.Count == 0)
                    throw new InvalidOperationException(
                        "The decomposition graph has pending tasks but no ready nodes.");

                var upstreamArtifacts = decompositionResults
                    .Where(item => item.Succeeded && item.Artifact is not null)
                    .Select(item => item.Artifact!)
                    .ToArray();
                var pendingIds = pendingParents
                    .Select(item => item.Id)
                    .ToHashSet(StringComparer.Ordinal);
                var waveParents = readyParents
                    .Where(parent => pendingIds.Contains(parent.Id))
                    .ToArray();
                var waveTasks = waveParents
                    .Select(parent => workflow.StepAsync<
                        CodeGenerationDecompositionWorkflowRequest,
                        CodeGenerationDecompositionWorkflowResult>(
                        $"decomposition/{planningResult.ArchitectureVersion}/{parent.Id}",
                        CodeGenerationWorkflowConstants.DecomposeTaskStep,
                        new CodeGenerationDecompositionWorkflowRequest(
                            planningResult.Plan,
                            parent.Id,
                            upstreamArtifacts,
                            planningResult.ArchitectureVersion,
                            planningResult.ArchitectureArtifact),
                        new StepOptions
                        {
                            ExecutionTimeout = TimeSpan.FromMinutes(15),
                            Retry = new RetryPolicy
                            {
                                MaxAttempts =
                                    CodeGenerationWorkflowConstants
                                        .MaximumDecompositionAttempts
                            }
                        },
                        cancellationToken))
                    .ToArray();
                var waveResults = await Task.WhenAll(waveTasks);
                decompositionResults.AddRange(waveResults);

                var failed = waveResults.FirstOrDefault(item =>
                    !item.Succeeded &&
                    item.Decomposition?.Status !=
                        TaskDecompositionStatus.ArchitectureGap);
                if (failed is not null)
                    return MergeDecompositionResults(
                        planningResult,
                        decompositionResults,
                        failed);

                architectureGap = waveResults.FirstOrDefault(item =>
                    !item.Succeeded &&
                    item.Decomposition?.Status ==
                        TaskDecompositionStatus.ArchitectureGap);
                if (architectureGap is not null)
                    break;
            }

            if (architectureGap is null)
                break;

            if (!decompositionArchitectureIntegrationBudget.TryConsume(
                    architectureGap.ParentTaskId,
                    out var targetIntegrationAttempt))
            {
                return MergeDecompositionResults(
                    planningResult,
                    decompositionResults,
                    architectureGap) with
                {
                    Failure = "ArchitectureDecisionIntegrationLimitReached",
                    Error = $"Decomposition target '{architectureGap.ParentTaskId}' still reports an architecture gap after {targetIntegrationAttempt - 1} focused decision-integration cycles."
                };
            }

            var gapReview = CreateArchitectureGapReview(architectureGap);
            var gapSequence = await ResolveArchitectureFindingsSequentiallyAsync(
                workflow,
                architecturePlan,
                gapReview,
                architectureResult,
                architectureReviews,
                architectureIntegrations,
                cancellationToken);
            architectureResult = gapSequence.Result;
            if (!gapSequence.Completed)
                return architectureResult;
            architecturePlan = gapSequence.Plan;
            var coherenceReviewNeededIntegration = false;
            var integrationApproved = false;
            for (var pass = 1;
                 pass <= CodeGenerationWorkflowConstants.MaximumArchitectureReviewPasses;
                 pass++)
            {
                var review = await workflow.StepAsync<
                    ArchitectureReviewWorkflowRequest,
                    ArchitectureReviewWorkflowResult>(
                    $"architecture-review/{architectureResult.ArchitectureVersion}/{pass}",
                    CodeGenerationWorkflowConstants.ReviewArchitectureStep,
                    new ArchitectureReviewWorkflowRequest(
                        architecturePlan,
                        pass,
                        architectureReviews.LastOrDefault()?.Review,
                        architectureResult.ArchitectureVersion,
                        architectureResult.ArchitectureArtifact,
                        ArchitectureInputs(architectureResult)),
                    ArchitectureActivityOptions(),
                    cancellationToken);
                architectureReviews.Add(review);
                architectureResult = MergeArchitectureReview(
                    architectureResult,
                    architectureReviews,
                    architectureIntegrations,
                    review);
                if (!review.Succeeded || review.Review is null)
                    return architectureResult;
                if (CanAcceptArchitecture(review.Review, pass))
                {
                    integrationApproved = true;
                    break;
                }
                if (pass == CodeGenerationWorkflowConstants.MaximumArchitectureReviewPasses)
                {
                    return architectureResult with
                    {
                        Succeeded = false,
                        Failure = "ArchitectureReviewFailed",
                        Error = "The integrated architecture still has blocking findings after the maximum review passes."
                    };
                }

                coherenceReviewNeededIntegration = true;
                var coherenceSequence =
                    await ResolveArchitectureFindingsSequentiallyAsync(
                        workflow,
                        architecturePlan,
                        review.Review,
                        architectureResult,
                        architectureReviews,
                        architectureIntegrations,
                        cancellationToken);
                architectureResult = coherenceSequence.Result;
                if (!coherenceSequence.Completed)
                    return architectureResult;
                architecturePlan = coherenceSequence.Plan;
            }

            if (!integrationApproved)
                return architectureResult;

            planningResult = architectureResult with
            {
                Plan = architecturePlan,
                Succeeded = true,
                Failure = "None",
                Error = null
            };

            if (coherenceReviewNeededIntegration)
            {
                decompositionResults.Clear();
                continue;
            }

            var affectedIds = gapReview.Findings
                .SelectMany(item => item.AffectedIds)
                .ToHashSet(StringComparer.Ordinal);
            decompositionResults.RemoveAll(item =>
                !item.Succeeded ||
                IsAffectedByArchitectureIntegration(
                    architecturePlan,
                    item.ParentTaskId,
                    affectedIds));
        }

        var decomposedPlanningResult = MergeDecompositionResults(
            planningResult,
            decompositionResults,
            failed: null);

        var scaffoldingResult = await workflow.StepAsync<
            CodeGenerationScaffoldingRequest,
            CodeGenerationScaffoldingResult>(
                $"scaffolding/{planningResult.ArchitectureVersion}",
                CodeGenerationWorkflowConstants.ScaffoldStep,
                new CodeGenerationScaffoldingRequest(planningResult.Plan),
                new StepOptions
                {
                    ExecutionTimeout = TimeSpan.FromMinutes(10),
                    Retry = new RetryPolicy
                    {
                        MaxAttempts = 1
                    }
                },
                cancellationToken);

        var scaffoldedResult = decomposedPlanningResult with
        {
            Succeeded = scaffoldingResult.Succeeded,
            Failure = scaffoldingResult.Succeeded
                ? decomposedPlanningResult.Failure
                : "ScaffoldingFailed",
            Error = scaffoldingResult.Error,
            WrittenFiles = scaffoldingResult.Artifacts,
            Scaffolding = scaffoldingResult
        };

        if (!scaffoldingResult.Succeeded)
            return scaffoldedResult;

        var taskResults = new List<CodeGenerationTaskWorkflowResult>();
        var decompositionsByParent = decompositionResults
            .Where(item => item.Succeeded && item.Decomposition is not null)
            .ToDictionary(
                item => item.ParentTaskId,
                item => item.Decomposition!,
                StringComparer.Ordinal);
        var completedParentIds = planningResult.Plan.Tasks
            .Where(item => item.ExecutionKind ==
                PlanTaskExecutionKind.Scaffolding)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var completedLeafIds = architectureTasks.ToDictionary(
            parent => parent.Id,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var remainingLeafCount = decompositionsByParent.Values
            .Sum(item => item.LeafTasks.Count);

        while (remainingLeafCount > 0)
        {
            var readyParents = GetReadyCodeGenerationTasks(
                planningResult.Plan,
                completedParentIds);
            var readyNodes = readyParents
                .SelectMany(parent => GetReadyLeafTasks(
                        decompositionsByParent[parent.Id],
                        completedLeafIds[parent.Id])
                    .Select(leaf => (Parent: parent, Leaf: leaf)))
                .ToArray();
            if (readyNodes.Length == 0)
                throw new InvalidOperationException(
                    "The implementation graph has pending tasks but no ready nodes.");

            var waveTasks = readyNodes
                .Select(node => workflow.StepAsync<
                    CodeGenerationTaskWorkflowRequest,
                    CodeGenerationTaskWorkflowResult>(
                        $"generation/{node.Parent.Id}/{node.Leaf.Id}",
                        CodeGenerationWorkflowConstants.GenerateTaskStep,
                        new CodeGenerationTaskWorkflowRequest(
                            planningResult.Plan,
                            node.Parent.Id,
                            node.Leaf,
                            RepositoryContext: planningResult.RepositoryContext),
                        TaskActivityOptions(startingModelTier: 1),
                        cancellationToken))
                .ToArray();
            var waveResults = await Task.WhenAll(waveTasks);
            taskResults.AddRange(waveResults);

            var failedTask = waveResults.FirstOrDefault(item =>
                !item.Succeeded);
            if (failedTask is not null)
                return MergeResults(
                    scaffoldedResult,
                    taskResults,
                    failedTask);

            for (var index = 0; index < readyNodes.Length; index++)
            {
                var node = readyNodes[index];
                completedLeafIds[node.Parent.Id].Add(node.Leaf.Id);
                remainingLeafCount--;
            }

            foreach (var parent in readyParents)
            {
                var decomposition = decompositionsByParent[parent.Id];
                if (decomposition.LeafTasks.All(item =>
                    completedLeafIds[parent.Id].Contains(item.Id)))
                {
                    completedParentIds.Add(parent.Id);
                }
            }
        }

        var generatedResult = MergeResults(
            scaffoldedResult,
            taskResults,
            failedTask: null);
        await SaveCheckpointAsync(
            workflow,
            request.Prompt,
            generatedResult,
            "generated",
            cancellationToken);
        var finalResult = await RunBuildAndRepairAsync(
            workflow,
            generatedResult,
            cancellationToken);
        await SaveCheckpointAsync(
            workflow,
            request.Prompt,
            finalResult,
            "final",
            cancellationToken);
        return finalResult;
    }

    private async Task<CodeGenerationWorkflowResult> ContinueAsync(
        CodeGenerationWorkflowRequest request,
        WorkflowContext workflow,
        CancellationToken cancellationToken)
    {
        if (request.ContinuationMode !=
            CodeGenerationContinuationMode.BuildAndRepair)
        {
            throw new InvalidOperationException(
                $"Continuation mode '{request.ContinuationMode}' is not supported.");
        }

        if (string.IsNullOrWhiteSpace(request.ResumeFromWorkflowId))
            throw new InvalidOperationException(
                "BuildAndRepair continuation requires a source workflow ID.");
        if (request.ResumeFromWorkflowId.Equals(
                workflow.WorkflowRunId.ToString("D"),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A workflow cannot continue from itself.");
        }

        var checkpoint = await workflow.StepAsync<
            CodeGenerationCheckpointLoadRequest,
            CodeGenerationRunCheckpoint>(
            "continuation/load-checkpoint",
            CodeGenerationWorkflowConstants.LoadCheckpointStep,
            new CodeGenerationCheckpointLoadRequest(
                request.ResumeFromWorkflowId,
                request.Prompt,
                request.ResumeFallback),
            CheckpointActivityOptions(),
            cancellationToken);
        var resumed = checkpoint.Result with
        {
            Succeeded = true,
            Failure = "None",
            Error = null,
            Build = null,
            BuildAttempts = [],
            BuildRepairs = [],
            Continuation = new CodeGenerationContinuationInfo(
                request.ResumeFromWorkflowId,
                request.ContinuationMode)
        };
        await SaveCheckpointAsync(
            workflow,
            checkpoint.Prompt,
            resumed,
            "continuation-loaded",
            cancellationToken);
        var finalResult = await RunBuildAndRepairAsync(
            workflow,
            resumed,
            cancellationToken);
        await SaveCheckpointAsync(
            workflow,
            checkpoint.Prompt,
            finalResult,
            "continuation-final",
            cancellationToken);
        return finalResult;
    }

    private async Task<CodeGenerationWorkflowResult>
        RunBuildAndRepairAsync(
            WorkflowContext workflow,
            CodeGenerationWorkflowResult generatedResult,
            CancellationToken cancellationToken)
    {
        var currentResult = generatedResult;
        var buildAttempts = new List<CodeGenerationBuildResult>();
        var buildRepairs = new List<CodeGenerationTaskWorkflowResult>();

        for (var buildAttempt = 1;
             buildAttempt <=
                 CodeGenerationWorkflowConstants.MaximumBuildAttempts;
             buildAttempt++)
        {
            var buildResult = await workflow.StepAsync<
                CodeGenerationBuildRequest,
                CodeGenerationBuildResult>(
                    $"build/{buildAttempt}",
                    CodeGenerationWorkflowConstants.BuildStep,
                    new CodeGenerationBuildRequest(
                        currentResult.WrittenFiles,
                        currentResult.Plan!.Solution.Path,
                        buildAttempt,
                        CodeGenerationWorkflowConstants
                            .MaximumBuildAttempts),
                    new StepOptions
                    {
                        ExecutionTimeout = TimeSpan.FromMinutes(15),
                        Retry = new RetryPolicy
                        {
                            MaxAttempts = 1
                        }
                    },
                    cancellationToken);
            buildAttempts.Add(buildResult);

            if (buildResult.Succeeded ||
                buildAttempt ==
                    CodeGenerationWorkflowConstants.MaximumBuildAttempts)
            {
                return ApplyBuildResult(
                    currentResult,
                    buildResult,
                    buildAttempts,
                    buildRepairs);
            }

            var repairRequests = CodeGenerationBuildRepairPlanner.Create(
                currentResult.Plan!,
                buildResult,
                currentResult.TaskResults,
                currentResult.Decompositions,
                repairCycle: buildAttempt,
                previousBuild: buildAttempts.Count > 1
                    ? buildAttempts[^2]
                    : null)
                .Select(item => item with
                {
                    RepositoryContext = currentResult.RepositoryContext
                })
                .ToArray();
            if (repairRequests.Length == 0)
            {
                var stalled = ApplyBuildResult(
                    currentResult,
                    buildResult,
                    buildAttempts,
                    buildRepairs);
                return buildAttempt == 1
                    ? stalled
                    : stalled with
                    {
                        Failure = "BuildRepairNoProgress",
                        Error = $"{buildResult.Error} The latest build did not expose any new repair targets; unchanged repairs will not be repeated."
                    };
            }

            foreach (var repairRequest in repairRequests)
            {
                var repairResult = await workflow.StepAsync<
                    CodeGenerationTaskWorkflowRequest,
                    CodeGenerationTaskWorkflowResult>(
                    $"build-repair/{buildAttempt}/{repairRequest.ParentTaskId}/{repairRequest.Task.Id}",
                    CodeGenerationWorkflowConstants.GenerateTaskStep,
                    repairRequest,
                    TaskActivityOptions(
                        repairRequest.StartingModelTier),
                    cancellationToken);
                buildRepairs.Add(repairResult);
                currentResult = MergeBuildRepairResult(
                    currentResult,
                    repairResult,
                    buildRepairs);

                if (!repairResult.Succeeded)
                {
                    return currentResult with
                    {
                        Succeeded = false,
                        Failure = "BuildRepairFailed",
                        Error = repairResult.Error,
                        Build = buildResult,
                        BuildAttempts = buildAttempts,
                        BuildRepairs = buildRepairs
                    };
                }
            }
        }

        throw new InvalidOperationException(
            "The build loop ended without a terminal result.");
    }

    private async Task SaveCheckpointAsync(
        WorkflowContext workflow,
        string prompt,
        CodeGenerationWorkflowResult result,
        string checkpointKey,
        CancellationToken cancellationToken) =>
        _ = await workflow.StepAsync<
            CodeGenerationCheckpointRequest,
            Guyabano.Artifacts.ArtifactReference>(
            $"checkpoint/{checkpointKey}",
            CodeGenerationWorkflowConstants.SaveCheckpointStep,
            new CodeGenerationCheckpointRequest(
                workflow.WorkflowRunId.ToString("D"),
                prompt,
                result),
            CheckpointActivityOptions(),
            cancellationToken);

    private static async Task<CodeGenerationWorkflowRequest>
        PrepareRepositoryContextAsync(
            WorkflowContext workflow,
            CodeGenerationWorkflowRequest request,
            CancellationToken cancellationToken)
    {
        if (request.Repository is null)
            return request;

        var revision = await workflow.StepAsync<
            RepositoryIndexRequest,
            RepositoryRevision>(
                "repository/index",
                CodeGenerationWorkflowConstants.IndexRepositoryStep,
                new RepositoryIndexRequest(
                    request.Repository,
                    workflow.WorkflowRunId.ToString("D"),
                    request.SessionId.ToString()),
                RepositoryContextActivityOptions(TimeSpan.FromMinutes(15)),
                cancellationToken);
        var selection = await workflow.StepAsync<
            RepositoryContextSelectionRequest,
            RepositoryContextSelection>(
                "repository/select",
                CodeGenerationWorkflowConstants.SelectRepositoryContextStep,
                new RepositoryContextSelectionRequest(
                    revision,
                    request.Repository.SymbolSeeds ?? []),
                RepositoryContextActivityOptions(TimeSpan.FromMinutes(5)),
                cancellationToken);
        var captured = await workflow.StepAsync<
            RepositoryContextCaptureRequest,
            RepositoryContextReference>(
                "repository/capture",
                CodeGenerationWorkflowConstants.CaptureRepositoryContextStep,
                new RepositoryContextCaptureRequest(
                    selection,
                    workflow.WorkflowRunId.ToString("D"),
                    request.SessionId.ToString(),
                    request.Prompt),
                RepositoryContextActivityOptions(TimeSpan.FromMinutes(2)),
                cancellationToken);
        return request with { RepositoryContext = captured };
    }

    private static StepOptions RepositoryContextActivityOptions(
        TimeSpan timeout) => new()
    {
        ExecutionTimeout = timeout,
        Retry = new RetryPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2,
            MaximumDelay = TimeSpan.FromSeconds(10),
            MaxAttempts = 2
        }
    };

    private static StepOptions CheckpointActivityOptions() => new()
    {
        ExecutionTimeout = TimeSpan.FromMinutes(2),
        Retry = new RetryPolicy
        {
            MaxAttempts = 2
        }
    };

    internal static bool CanAcceptArchitecture(
        ArchitectureReview review,
        int reviewPass)
    {
        ArgumentNullException.ThrowIfNull(review);
        return review.Findings.Count == 0 ||
            reviewPass ==
                CodeGenerationWorkflowConstants.MaximumArchitectureReviewPasses &&
            review.Approved;
    }

    internal static ArchitectureReview CreateArchitectureGapReview(
        CodeGenerationDecompositionWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var gaps = result.Decomposition?.ArchitectureGaps ?? [];
        if (gaps.Count == 0)
            throw new InvalidOperationException(
                "An architecture-gap decomposition must contain gap details.");

        return new ArchitectureReview
        {
            Approved = false,
            Findings = gaps.Select((gap, index) =>
                new ArchitectureReviewFinding
                {
                    Id = $"decomposition-gap-{result.ParentTaskId}-{index + 1}",
                    Severity = ArchitectureReviewSeverity.Blocking,
                    Category = "DecompositionArchitectureGap",
                    Summary = gap.Question,
                    Evidence =
                    [
                        $"Decomposition task: {result.ParentTaskId}",
                        gap.Reason
                    ],
                    AffectedIds = gap.AffectedContractIds
                        .Concat(gap.AffectedDecisionIds)
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    SuggestedResolution = gap.Question,
                    RequiresUserInput = false
                }).ToList()
        };
    }

    private sealed record ArchitectureDecisionSequenceResult(
        CodeGenerationPlan Plan,
        CodeGenerationWorkflowResult Result,
        bool Completed);

    private async Task<ArchitectureDecisionSequenceResult>
        ResolveArchitectureFindingsSequentiallyAsync(
            WorkflowContext workflow,
            CodeGenerationPlan startingPlan,
            ArchitectureReview review,
            CodeGenerationWorkflowResult startingResult,
            IReadOnlyList<ArchitectureReviewWorkflowResult> reviews,
            List<ArchitectureDecisionIntegrationWorkflowResult> integrations,
            CancellationToken cancellationToken)
    {
        if (review.Findings.Count == 0)
            return new(startingPlan, startingResult, true);

        var plan = startingPlan;
        var result = startingResult;
        foreach (var finding in review.Findings)
        {
            var resolution = await workflow.StepAsync<
                ArchitectureGapResolutionWorkflowRequest,
                ArchitectureGapResolutionWorkflowResult>(
                $"architecture-gap/{result.ArchitectureVersion}/{finding.Id}",
                CodeGenerationWorkflowConstants.ResolveArchitectureGapStep,
                new ArchitectureGapResolutionWorkflowRequest(
                    plan,
                    finding,
                    result.ArchitectureVersion,
                    result.ArchitectureArtifact,
                    ArchitectureInputs(result),
                    result.ArchitecturePractices),
                ArchitectureActivityOptions(),
                cancellationToken);
            result = MergeArchitectureResolutions(result, [resolution]);
            if (!resolution.Succeeded || resolution.Resolution is null)
                return new(
                    plan,
                    result with
                    {
                        Succeeded = false,
                        Failure = resolution.Failure,
                        Error = resolution.Error
                    },
                    false);
            if (resolution.Resolution.RequiresUserInput)
                return new(
                    plan,
                    result with
                    {
                        Succeeded = false,
                        Failure = "ArchitectureClarificationRequired",
                        Error = resolution.Resolution.UserQuestion
                    },
                    false);

            var focusedReview = new ArchitectureReview
            {
                Approved = false,
                Findings = [finding]
            };
            var resolvedReview = ApplyResolutions(
                focusedReview,
                [resolution]);
            var integration = await workflow.StepAsync<
                ArchitectureDecisionIntegrationWorkflowRequest,
                ArchitectureDecisionIntegrationWorkflowResult>(
                $"architecture-integration/{result.ArchitectureVersion}/{finding.Id}",
                CodeGenerationWorkflowConstants.IntegrateArchitectureStep,
                new ArchitectureDecisionIntegrationWorkflowRequest(
                    plan,
                    resolvedReview,
                    [resolution.Resolution],
                    result.ArchitectureVersion,
                    result.ArchitectureArtifact,
                    resolution.Artifact is null
                        ? []
                        : [resolution.Artifact]),
                ArchitectureActivityOptions(),
                cancellationToken);
            integrations.Add(integration);
            result = MergeArchitectureDecisionIntegration(
                result,
                reviews,
                integrations,
                integration);
            if (!integration.Succeeded || integration.IntegratedPlan is null)
                return new(plan, result, false);

            plan = integration.IntegratedPlan;
        }

        return new(plan, result, true);
    }

    private static IReadOnlyList<Guyabano.Artifacts.ArtifactReference>
        ArchitectureInputs(CodeGenerationWorkflowResult result) =>
        result.PlanningArtifacts
            .Concat(result.ArchitectureResolutions
                .Where(item => item.Artifact is not null)
                .Select(item => item.Artifact!))
            .Concat(result.ArchitectureDecisionIntegrations
                .Where(item => item.Artifact is not null)
                .Select(item => item.Artifact!))
            .DistinctBy(item => item.ArtifactId, StringComparer.Ordinal)
            .ToArray();

    internal static ArchitectureReview ApplyResolutions(
        ArchitectureReview review,
        IReadOnlyList<ArchitectureGapResolutionWorkflowResult> results)
    {
        var byFinding = results
            .Where(item => item.Resolution is not null)
            .ToDictionary(
                item => item.Resolution!.FindingId,
                item => item.Resolution!,
                StringComparer.Ordinal);
        return new ArchitectureReview
        {
            Approved = review.Approved,
            Findings = review.Findings.Select(finding =>
            {
                if (!byFinding.TryGetValue(finding.Id, out var resolution))
                    return finding;
                var explanation = string.Join(" ",
                    new[]
                    {
                        resolution.Decision,
                        $"Authoritative ADR {resolution.DecisionRecord.Id}: {resolution.DecisionRecord.Title}. {resolution.DecisionRecord.Decision}",
                        $"Applied architecture practice {resolution.AppliedPractice.Id}: {resolution.AppliedPractice.Guidance}",
                        $"Reasons: {string.Join("; ", resolution.Reasons)}.",
                        $"Consequences: {string.Join("; ", resolution.Consequences)}.",
                        resolution.AlternativesConsidered.Count == 0
                            ? string.Empty
                            : $"Alternatives considered: {string.Join("; ", resolution.AlternativesConsidered)}."
                    }.Where(item => !string.IsNullOrWhiteSpace(item)));
                return new ArchitectureReviewFinding
                {
                    Id = finding.Id,
                    Severity = finding.Severity,
                    Category = finding.Category,
                    Summary = finding.Summary,
                    Evidence = finding.Evidence.Concat(
                        [$"Focused resolution kind: {resolution.ResolutionKind}"])
                        .ToList(),
                    AffectedIds = finding.AffectedIds
                        .Concat(resolution.AffectedIds)
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    SuggestedResolution = explanation,
                    RequiresUserInput = resolution.RequiresUserInput
                };
            }).ToList()
        };
    }

    internal static CodeGenerationWorkflowResult MergeArchitectureResolutions(
        CodeGenerationWorkflowResult current,
        IReadOnlyList<ArchitectureGapResolutionWorkflowResult> latest)
    {
        var attempts = current.JsonRepairAttempts.Concat(
            latest.SelectMany(result => result.JsonRepairAttempts.Select(
                attempt => attempt with
                {
                    Name = $"architecture/resolution-{result.Resolution?.FindingId ?? "unknown"}/{attempt.Name}"
                }))).ToArray();
        var failure = latest.FirstOrDefault(item => !item.Succeeded);
        return current with
        {
            Succeeded = failure is null,
            Failure = failure?.Failure ?? current.Failure,
            Error = failure?.Error,
            Model = latest.LastOrDefault()?.Model ?? current.Model,
            JsonWasRepaired = current.JsonWasRepaired ||
                latest.Any(item => item.JsonWasRepaired),
            JsonRepairAttempts = attempts,
            Usage = AggregateUsage(current.Usage, latest.Select(item => item.Usage)),
            Diagnostics = failure?.Diagnostics ?? current.Diagnostics,
            FinishReason = failure?.FinishReason ?? current.FinishReason,
            ArchitectureResolutions = current.ArchitectureResolutions
                .Concat(latest)
                .ToArray(),
            ArchitecturePractices = current.ArchitecturePractices
                .Concat(latest
                    .Where(item =>
                        item.Resolution is not null &&
                        !item.Resolution.ReusedExistingPractice)
                    .Select(item => item.Resolution!.AppliedPractice))
                .DistinctBy(item => item.Id, StringComparer.Ordinal)
                .ToArray()
        };
    }

    internal static bool IsAffectedByArchitectureIntegration(
        CodeGenerationPlan plan,
        string taskId,
        IReadOnlySet<string> affectedIds)
    {
        var task = plan.Tasks.SingleOrDefault(item =>
            item.Id.Equals(taskId, StringComparison.Ordinal));
        return task is null || task.ContractIds.Any(affectedIds.Contains) ||
            task.DecisionIds.Any(affectedIds.Contains);
    }

    private static StepOptions ArchitectureActivityOptions() =>
        new()
        {
            ExecutionTimeout = TimeSpan.FromMinutes(15),
            Retry = new RetryPolicy
            {
                MaxAttempts =
                    CodeGenerationWorkflowConstants
                        .MaximumArchitectureTransportAttempts
            }
        };

    private static StepOptions TaskActivityOptions(
        int startingModelTier) =>
        new()
        {
            ExecutionTimeout = TimeSpan.FromMinutes(15),
            Retry = new RetryPolicy
            {
                MaxAttempts =
                    (CodeGenerationWorkflowConstants.MaximumModelTiers -
                        startingModelTier + 1) *
                    CodeGenerationWorkflowConstants.MaximumAttemptsPerModel
            }
        };

    internal static CodeGenerationWorkflowResult ApplyBuildResult(
        CodeGenerationWorkflowResult generatedResult,
        CodeGenerationBuildResult buildResult,
        IReadOnlyList<CodeGenerationBuildResult>? buildAttempts = null,
        IReadOnlyList<CodeGenerationTaskWorkflowResult>? buildRepairs = null) =>
        generatedResult with
        {
            Succeeded = buildResult.Succeeded,
            Failure = buildResult.Succeeded
                ? "None"
                : "CompilationFailed",
            Error = buildResult.Error,
            Build = buildResult,
            BuildAttempts = buildAttempts ?? [buildResult],
            BuildRepairs = buildRepairs ?? generatedResult.BuildRepairs
        };

    internal static IReadOnlyList<GenerationTaskPlan>
        OrderCodeGenerationTasks(CodeGenerationPlan plan)
    {
        var completed = plan.Tasks
            .Where(task =>
                task.ExecutionKind == PlanTaskExecutionKind.Scaffolding)
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);
        var remaining = plan.Tasks
            .Where(task =>
                task.ExecutionKind == PlanTaskExecutionKind.CodeGeneration)
            .ToList();
        var ordered = new List<GenerationTaskPlan>(remaining.Count);

        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(task =>
                task.DependsOn.All(completed.Contains));
            if (ready is null)
                throw new InvalidOperationException(
                    "The plan contains code-generation tasks whose dependencies cannot be scheduled.");

            ordered.Add(ready);
            completed.Add(ready.Id);
            remaining.Remove(ready);
        }

        return ordered;
    }

    internal static IReadOnlyList<GenerationTaskPlan>
        GetReadyCodeGenerationTasks(
            CodeGenerationPlan plan,
            IReadOnlySet<string> completedTaskIds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(completedTaskIds);

        return plan.Tasks
            .Where(task =>
                task.ExecutionKind == PlanTaskExecutionKind.CodeGeneration &&
                !completedTaskIds.Contains(task.Id) &&
                task.DependsOn.All(completedTaskIds.Contains))
            .ToArray();
    }

    internal static IReadOnlyList<CodeGenerationLeafTask>
        OrderLeafTasks(CodeGenerationTaskDecomposition decomposition)
    {
        if (decomposition.Status != TaskDecompositionStatus.Ready)
            return [];

        var remaining = decomposition.LeafTasks.ToList();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<CodeGenerationLeafTask>(remaining.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(item =>
                item.DependsOn.All(completed.Contains));
            if (ready is null)
                throw new InvalidOperationException(
                    $"Task '{decomposition.ParentTaskId}' contains leaf dependencies that cannot be scheduled.");
            ordered.Add(ready);
            completed.Add(ready.Id);
            remaining.Remove(ready);
        }

        return ordered;
    }

    internal static IReadOnlyList<CodeGenerationLeafTask> GetReadyLeafTasks(
        CodeGenerationTaskDecomposition decomposition,
        IReadOnlySet<string> completedTaskIds)
    {
        ArgumentNullException.ThrowIfNull(decomposition);
        ArgumentNullException.ThrowIfNull(completedTaskIds);

        if (decomposition.Status != TaskDecompositionStatus.Ready)
            return [];

        return decomposition.LeafTasks
            .Where(task =>
                !completedTaskIds.Contains(task.Id) &&
                task.DependsOn.All(completedTaskIds.Contains))
            .ToArray();
    }

    private static CodeGenerationWorkflowResult MergeDecompositionResults(
        CodeGenerationWorkflowResult planningResult,
        IReadOnlyList<CodeGenerationDecompositionWorkflowResult> results,
        CodeGenerationDecompositionWorkflowResult? failed)
    {
        var attempts = planningResult.JsonRepairAttempts
            .Concat(results.SelectMany(result =>
                result.JsonRepairAttempts.Select(attempt =>
                    attempt with
                    {
                        Name =
                            $"decomposition/{result.ParentTaskId}/{attempt.Name}"
                    })))
            .ToArray();

        return planningResult with
        {
            Succeeded = failed is null,
            Failure = failed?.Failure ?? planningResult.Failure,
            Error = failed?.Error,
            Model = failed?.Model ?? planningResult.Model,
            JsonWasRepaired = planningResult.JsonWasRepaired ||
                results.Any(item => item.JsonWasRepaired),
            JsonRepairAttempts = attempts,
            Usage = AggregateUsage(
                planningResult.Usage,
                results.Select(item => item.Usage)),
            Diagnostics = failed?.Diagnostics ?? planningResult.Diagnostics,
            FinishReason = failed?.FinishReason,
            Decompositions = results
        };
    }

    private static CodeGenerationWorkflowResult MergeArchitectureReview(
        CodeGenerationWorkflowResult current,
        IReadOnlyList<ArchitectureReviewWorkflowResult> reviews,
        IReadOnlyList<ArchitectureDecisionIntegrationWorkflowResult> integrations,
        ArchitectureReviewWorkflowResult latest)
    {
        var attempts = current.JsonRepairAttempts
            .Concat(latest.JsonRepairAttempts.Select(attempt =>
                attempt with
                {
                    Name =
                        $"architecture/review-{latest.ReviewPass}/{attempt.Name}"
                }))
            .ToArray();
        return current with
        {
            Succeeded = latest.Succeeded,
            Failure = latest.Succeeded ? current.Failure : latest.Failure,
            Error = latest.Error,
            Model = latest.Model,
            JsonWasRepaired = current.JsonWasRepaired || latest.JsonWasRepaired,
            JsonRepairAttempts = attempts,
            Usage = AggregateUsage(current.Usage, [latest.Usage]),
            Diagnostics = latest.Diagnostics ?? current.Diagnostics,
            FinishReason = latest.FinishReason,
            ArchitectureArtifact = latest.Artifact ??
                current.ArchitectureArtifact,
            ArchitectureReviews = reviews,
            ArchitectureDecisionIntegrations = integrations
        };
    }

    private static CodeGenerationWorkflowResult MergeArchitectureDecisionIntegration(
        CodeGenerationWorkflowResult current,
        IReadOnlyList<ArchitectureReviewWorkflowResult> reviews,
        IReadOnlyList<ArchitectureDecisionIntegrationWorkflowResult> integrations,
        ArchitectureDecisionIntegrationWorkflowResult latest)
    {
        var attempts = current.JsonRepairAttempts
            .Concat(latest.JsonRepairAttempts.Select(attempt =>
                attempt with
                {
                    Name =
                        $"architecture/integration-{latest.ArchitectureVersion}/{attempt.Name}"
                }))
            .ToArray();
        return current with
        {
            Succeeded = latest.Succeeded,
            Failure = latest.Succeeded ? current.Failure : latest.Failure,
            Error = latest.Error,
            Model = latest.Model,
            Plan = latest.IntegratedPlan ?? current.Plan,
            ArchitectureVersion = latest.Succeeded
                ? latest.ArchitectureVersion
                : current.ArchitectureVersion,
            JsonWasRepaired = current.JsonWasRepaired || latest.JsonWasRepaired,
            JsonRepairAttempts = attempts,
            Usage = AggregateUsage(current.Usage, [latest.Usage]),
            Diagnostics = latest.Diagnostics ?? current.Diagnostics,
            FinishReason = latest.FinishReason,
            ArchitectureReviews = reviews,
            ArchitectureDecisionIntegrations = integrations
        };
    }

    private static CodeGenerationWorkflowResult MergeResults(
        CodeGenerationWorkflowResult scaffoldedResult,
        IReadOnlyList<CodeGenerationTaskWorkflowResult> taskResults,
        CodeGenerationTaskWorkflowResult? failedTask)
    {
        var attempts = scaffoldedResult.JsonRepairAttempts
            .Concat(taskResults.SelectMany(task =>
                task.JsonRepairAttempts.Select(attempt =>
                    attempt with
                    {
                        Name = $"{task.TaskId}/{attempt.Name}"
                    })))
            .ToArray();

        var files = scaffoldedResult.WrittenFiles
            .Concat(taskResults.SelectMany(task => task.WrittenFiles))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var skipped = scaffoldedResult.SkippedFiles
            .Concat(taskResults.SelectMany(task => task.SkippedFiles))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return scaffoldedResult with
        {
            Succeeded = failedTask is null,
            Failure = failedTask?.Failure ?? "None",
            Error = failedTask?.Error,
            Model = taskResults.LastOrDefault()?.Model ??
                scaffoldedResult.Model,
            JsonWasRepaired = scaffoldedResult.JsonWasRepaired ||
                taskResults.Any(task => task.JsonWasRepaired),
            JsonRepairAttempts = attempts,
            WrittenFiles = files,
            SkippedFiles = skipped,
            Usage = AggregateUsage(
                scaffoldedResult.Usage,
                taskResults.Select(task => task.Usage)),
            Diagnostics = failedTask?.Diagnostics ??
                scaffoldedResult.Diagnostics,
            FinishReason = failedTask?.FinishReason,
            TaskResults = taskResults
        };
    }

    private static CodeGenerationWorkflowResult MergeBuildRepairResult(
        CodeGenerationWorkflowResult current,
        CodeGenerationTaskWorkflowResult repair,
        IReadOnlyList<CodeGenerationTaskWorkflowResult> repairs)
    {
        var attempts = current.JsonRepairAttempts
            .Concat(repair.JsonRepairAttempts.Select(attempt =>
                attempt with
                {
                    Name =
                        $"build-repair/{repair.BuildRepairCycle}/{repair.TaskId}/{attempt.Name}"
                }))
            .ToArray();

        return current with
        {
            Succeeded = repair.Succeeded,
            Failure = repair.Succeeded ? "None" : repair.Failure,
            Error = repair.Error,
            Model = repair.Model,
            JsonWasRepaired = current.JsonWasRepaired ||
                repair.JsonWasRepaired,
            JsonRepairAttempts = attempts,
            WrittenFiles = current.WrittenFiles
                .Concat(repair.WrittenFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SkippedFiles = current.SkippedFiles
                .Concat(repair.SkippedFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Usage = AggregateUsage(current.Usage, [repair.Usage]),
            Diagnostics = repair.Diagnostics ?? current.Diagnostics,
            FinishReason = repair.FinishReason,
            TaskResults = current.TaskResults.Concat([repair]).ToArray(),
            BuildRepairs = repairs
        };
    }

    private static CodeGenerationUsage? AggregateUsage(
        CodeGenerationUsage? planningUsage,
        IEnumerable<CodeGenerationUsage?> taskUsages)
    {
        var values = taskUsages
            .Prepend(planningUsage)
            .Where(usage => usage is not null)
            .Cast<CodeGenerationUsage>()
            .ToArray();
        if (values.Length == 0)
            return null;

        return new CodeGenerationUsage(
            Sum(values.Select(usage => usage.PromptTokens)),
            Sum(values.Select(usage => usage.CompletionTokens)),
            Sum(values.Select(usage => usage.TotalTokens)),
            Sum(values.Select(usage => usage.PromptCacheHitTokens)),
            Sum(values.Select(usage => usage.PromptCacheMissTokens)));
    }

    private static int? Sum(IEnumerable<int?> values)
    {
        var present = values.Where(value => value.HasValue).ToArray();
        return present.Length == 0
            ? null
            : present.Sum(value => value!.Value);
    }
}
