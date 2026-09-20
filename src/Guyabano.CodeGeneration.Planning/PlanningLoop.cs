using Guyabano.Artifacts;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Host-side owner of the PLAN -> EXPAND -> EXECUTE -> EVIDENCE loop. Each
/// iteration observes catalog revisions and workflow state, asks the decider
/// for one decision, validates it deterministically, acts through the gap B
/// pipeline (propose -> apply -> author -> preserve -> execute), and persists
/// a checkpoint. Policy bounds end the run with typed outcomes; the workflow
/// itself never rewrites itself.
/// </summary>
public sealed class PlanningLoop(
    IPlanningDecider decider,
    WorkflowPatchProposer proposer,
    WorkflowAuthor author,
    IPlanningExecutionHost executionHost,
    IPlanningArtifactCatalog catalog,
    PlanningPolicy policy,
    PlanningStageRunner? stageRunner = null)
{
    private const int CheckpointSchemaVersion = 1;
    private const string CheckpointKind = "planning-checkpoint";
    private const string ProducedBy = "planning-loop";

    /// <summary>
    /// Runs the loop from an initial admitted design and DSL until a finish
    /// decision is admitted or a policy bound or failure ends the run.
    /// Resumes from the latest checkpoint when one exists.
    /// </summary>
    public async Task<PlanningLoopOutcome> RunAsync(
        string workflowId,
        string goal,
        PlannedExecutionDesign initial,
        string initialDsl,
        ITrustedCatalogue catalogue,
        string catalogueSummary,
        string model,
        int maxTokens = 4000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialDsl);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var policyErrors = policy.Validate();
        if (policyErrors.Count > 0)
        {
            throw new ArgumentException(
                "Invalid planning policy: " + string.Join(" ", policyErrors),
                nameof(policy));
        }

        if (initial.Graph.Steps.Count > policy.MaxWorkflowNodes)
        {
            throw new ArgumentException(
                "Initial design exceeds MaxWorkflowNodes.",
                nameof(initial));
        }

        var restored = await RestoreAsync(
            workflowId, initial, initialDsl, catalogue, cancellationToken).ConfigureAwait(false);
        if (restored.Terminal is not null)
        {
            return restored.Terminal;
        }

        var state = restored.State!;
        while (true)
        {
            var iteration = state.Checkpoint.Iteration + 1;
            if (iteration > policy.MaxIterations)
            {
                return await FinishAsync(
                    state, PlanningLoopStatus.Exhausted,
                    $"Max iterations ({policy.MaxIterations}) reached.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (state.ModelCalls >= policy.MaxModelCalls)
            {
                return await FinishAsync(
                    state, PlanningLoopStatus.Exhausted,
                    $"Max model calls ({policy.MaxModelCalls}) reached.",
                    cancellationToken).ConfigureAwait(false);
            }

            var observation = await ObserveAsync(
                state, goal, iteration, cancellationToken).ConfigureAwait(false);
            var decision = await DecideAsync(
                state, observation, allowReobserve: true, cancellationToken)
                .ConfigureAwait(false);
            if (decision is null)
            {
                return await FinishAsync(
                    state, PlanningLoopStatus.Failed,
                    state.PendingFailure ?? "The decider produced no usable decision.",
                    cancellationToken).ConfigureAwait(false);
            }

            var shapeErrors = ValidateDecision(decision, state);
            if (shapeErrors.Count > 0)
            {
                return await FinishAsync(
                    state, PlanningLoopStatus.Failed,
                    string.Join(" ", shapeErrors),
                    cancellationToken).ConfigureAwait(false);
            }

            state.Log(
                $"{iteration}:{decision.Action.ToString().ToLowerInvariant()}" +
                $"[{string.Join(",", decision.MotivatingArtifacts)}]");

            if (decision.ProduceStages.Count > 0)
            {
                if (!await ProduceStagesAsync(
                        state, goal, decision, iteration, cancellationToken).ConfigureAwait(false))
                {
                    return await FinishAsync(
                        state,
                        state.PendingFailureIsExhaustion
                            ? PlanningLoopStatus.Exhausted
                            : PlanningLoopStatus.Failed,
                        state.PendingFailure ?? "Artifact production failed.",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            switch (decision.Action)
            {
                case PlanningAction.Finish:
                    if (decision.ProduceStages.Count > 0)
                    {
                        return await FinishAsync(
                            state, PlanningLoopStatus.Failed,
                            "Finish decisions must not request artifact production.",
                            cancellationToken).ConfigureAwait(false);
                    }

                    return await FinishAsync(
                            state, PlanningLoopStatus.Finished,
                            decision.FinishReason!,
                            cancellationToken).ConfigureAwait(false);

                case PlanningAction.Validate:
                    await AdvanceAsync(state, cancellationToken).ConfigureAwait(false);
                    break;

                case PlanningAction.Expand:
                case PlanningAction.Revise:
                    if (state.StructuralIterations >= policy.MaxPlanningDepth)
                    {
                        return await FinishAsync(
                            state, PlanningLoopStatus.Exhausted,
                            $"Max planning depth ({policy.MaxPlanningDepth}) reached.",
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (!await ExpandAsync(
                            state, goal, decision, catalogue, catalogueSummary,
                            model, maxTokens, cancellationToken).ConfigureAwait(false))
                    {
                        if (state.TerminalOverride is not null)
                        {
                            var terminal = state.TerminalOverride.Value;
                            return await FinishAsync(
                                state, terminal.Status, terminal.Reason, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        return await FinishAsync(
                            state,
                            state.PendingFailureIsExhaustion
                                ? PlanningLoopStatus.Exhausted
                                : PlanningLoopStatus.Failed,
                            state.PendingFailure ?? "Expansion failed.",
                            cancellationToken).ConfigureAwait(false);
                    }

                    break;

                default:
                    return await FinishAsync(
                        state, PlanningLoopStatus.Failed,
                        $"Unknown planning action '{decision.Action}'.",
                        cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<Restored> RestoreAsync(
        string workflowId,
        PlannedExecutionDesign initial,
        string initialDsl,
        ITrustedCatalogue catalogue,
        CancellationToken cancellationToken)
    {
        var key = new PlanningArtifactKey(CheckpointKind, workflowId);
        var record = await catalog.GetCurrentAsync(workflowId, key, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            var plan = await CompileAsync(catalogue, initialDsl, cancellationToken)
                .ConfigureAwait(false);
            if (plan is null)
            {
                throw new ArgumentException(
                    "Initial DSL does not compile.",
                    nameof(initialDsl));
            }

            var snapshot = await executionHost.ObserveAsync(workflowId, cancellationToken)
                .ConfigureAwait(false);
            var state = LoopState.CreateFresh(
                workflowId, initial, initialDsl, plan,
                snapshot.WorkflowVersion, snapshot.IsComplete, snapshot.EvidenceSummary,
                catalog, policy);
            await state.SaveAsync(PlanningLoopStatus.Running, null, increment: false, cancellationToken)
                .ConfigureAwait(false);
            return new Restored(state, null);
        }

        var checkpoint = await catalog.ReadPayloadAsync<PlanningCheckpoint>(
            record, cancellationToken).ConfigureAwait(false);
        if (checkpoint.Status != PlanningLoopStatus.Running)
        {
            return new Restored(null, new PlanningLoopOutcome(
                checkpoint.Status,
                checkpoint.TerminalReason ?? "Resumed a terminal loop.",
                checkpoint,
                checkpoint.Design));
        }

        if (!string.Equals(
                StagedExecutionDesignIdentity.Compute(checkpoint.Design),
                checkpoint.DesignFingerprint,
                StringComparison.Ordinal))
        {
            return new Restored(null, new PlanningLoopOutcome(
                PlanningLoopStatus.Failed,
                "Checkpoint design does not match its fingerprint.",
                checkpoint,
                checkpoint.Design));
        }

        var live = await executionHost.ObserveAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                live.WorkflowVersion, checkpoint.WorkflowVersion, StringComparison.Ordinal))
        {
            return new Restored(null, new PlanningLoopOutcome(
                PlanningLoopStatus.Failed,
                $"Workflow version moved from '{checkpoint.WorkflowVersion}' " +
                $"to '{live.WorkflowVersion}' while the loop was down.",
                checkpoint,
                checkpoint.Design));
        }

        var resumed = await CompileAsync(catalogue, checkpoint.Dsl, cancellationToken)
            .ConfigureAwait(false);
        if (resumed is null)
        {
            return new Restored(null, new PlanningLoopOutcome(
                PlanningLoopStatus.Failed,
                "Checkpoint DSL no longer compiles against the current catalogue.",
                checkpoint,
                checkpoint.Design));
        }

        return new Restored(
            LoopState.Resume(checkpoint, resumed, live, catalog, policy), null);
    }

    private async Task<PlanningLoopObservation> ObserveAsync(
        LoopState state,
        string goal,
        int iteration,
        CancellationToken cancellationToken)
    {
        var current = await catalog.ListCurrentAsync(state.WorkflowId, cancellationToken)
            .ConfigureAwait(false);
        var fresh = current
            .Where(record => !string.Equals(
                record.Key.Kind, CheckpointKind, StringComparison.Ordinal))
            .Select(record => record.Version.Value)
            .Where(version => !state.KnownRevisions.Contains(version))
            .OrderBy(version => version, StringComparer.Ordinal)
            .ToArray();
        foreach (var record in current)
        {
            state.KnownRevisions.Add(record.Version.Value);
        }

        return new PlanningLoopObservation(
            goal,
            PlannedExecutionDesignSummary.Render(state.Current),
            state.Fingerprint,
            fresh,
            SupersededPins(state.Current, current),
            state.WorkflowVersion,
            state.WorkflowComplete,
            state.Evidence,
            new PlanningLoopBudget(
                Math.Max(0, policy.MaxIterations - iteration + 1),
                Math.Max(0, policy.MaxMutations - state.Mutations),
                Math.Max(0, policy.MaxPlanningDepth - state.StructuralIterations),
                Math.Max(0, policy.MaxModelCalls - state.ModelCalls),
                policy.MaxTotalTokens is null || state.ReportedTokens is null
                    ? null
                    : Math.Max(0, policy.MaxTotalTokens.Value - state.ReportedTokens.Value)),
            state.Checkpoint);
    }

    /// <summary>
    /// Reports design pins the catalog has moved past, as
    /// <c>kind/name@current supersedes pinned kind/name@pinned</c> entries.
    /// This is the revise signal: it works on bootstrap (pre-existing changes)
    /// as well as mid-run, unlike arrival-only freshness.
    /// </summary>
    private static IReadOnlyList<string> SupersededPins(
        PlannedExecutionDesign design,
        IReadOnlyList<PlanningArtifactRecord> current)
    {
        var revisions = current
            .GroupBy(record => record.Key.Value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Max(record => record.Revision),
                StringComparer.Ordinal);
        var pins = design.Graph.Steps
            .SelectMany(step => step.RequiredArtifacts)
            .Concat(design.Bindings.Nodes
                .SelectMany(node => node.Binding.ContextArtifacts))
            .Distinct(StringComparer.Ordinal);
        var superseded = new List<string>();
        foreach (var pin in pins)
        {
            var at = pin.LastIndexOf('@');
            var key = at > 0 ? pin[..at] : pin;
            if (at <= 0 ||
                !int.TryParse(
                    pin[(at + 1)..],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var pinned) ||
                !revisions.TryGetValue(key, out var live) ||
                live <= pinned)
            {
                continue;
            }

            superseded.Add($"{key}@{live} supersedes pinned {pin}");
        }

        superseded.Sort(StringComparer.Ordinal);
        return superseded;
    }

    /// <summary>
    /// Asks the decider and validates the basis, re-observing once when the
    /// basis is stale. Returns null when no usable decision results.
    /// </summary>
    private async Task<PlanningDecision?> DecideAsync(
        LoopState state,
        PlanningLoopObservation observation,
        bool allowReobserve,
        CancellationToken cancellationToken)
    {
        var result = await decider.DecideAsync(observation, cancellationToken)
            .ConfigureAwait(false);
        state.ModelCalls += result.ModelCalls;
        if (!result.Succeeded || result.Decision is null)
        {
            state.PendingFailure = string.Join("; ", result.Diagnostics);
            return null;
        }

        if (!BasisMatches(result.Decision, state) && allowReobserve)
        {
            var refreshed = await executionHost.ObserveAsync(state.WorkflowId, cancellationToken)
                .ConfigureAwait(false);
            state.WorkflowVersion = refreshed.WorkflowVersion;
            state.WorkflowComplete = refreshed.IsComplete;
            state.Evidence = refreshed.EvidenceSummary;
            return await DecideAsync(
                state,
                observation with
                {
                    DesignFingerprint = state.Fingerprint,
                    WorkflowVersion = state.WorkflowVersion,
                    WorkflowComplete = state.WorkflowComplete,
                    EvidenceSummary = state.Evidence,
                },
                allowReobserve: false,
                cancellationToken).ConfigureAwait(false);
        }

        if (!BasisMatches(result.Decision, state))
        {
            state.PendingFailure = "Decision basis stayed stale after re-observation.";
            return null;
        }

        return result.Decision;
    }

    private static bool BasisMatches(PlanningDecision decision, LoopState state) =>
        string.Equals(decision.DesignFingerprint, state.Fingerprint, StringComparison.Ordinal) &&
        string.Equals(decision.WorkflowVersion, state.WorkflowVersion, StringComparison.Ordinal);

    private IReadOnlyList<string> ValidateDecision(PlanningDecision decision, LoopState state)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(decision.Rationale))
        {
            errors.Add("Decision must state a rationale.");
        }

        foreach (var revision in decision.ArtifactRevisions
                     .Where(revision => !state.KnownRevisions.Contains(revision))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(revision => revision, StringComparer.Ordinal))
        {
            errors.Add($"Decision observes unknown artifact revision '{revision}'.");
        }

        switch (decision.Action)
        {
            case PlanningAction.Finish:
                if (string.IsNullOrWhiteSpace(decision.FinishReason))
                {
                    errors.Add("Finish decisions must state a reason.");
                }

                break;
            case PlanningAction.Expand:
            case PlanningAction.Revise:
                if (decision.MotivatingArtifacts.Count == 0)
                {
                    errors.Add($"{decision.Action} decisions must name motivating artifacts.");
                }

                foreach (var revision in decision.MotivatingArtifacts
                             .Where(revision => !state.KnownRevisions.Contains(revision))
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(revision => revision, StringComparer.Ordinal))
                {
                    errors.Add($"Decision is motivated by unknown artifact revision '{revision}'.");
                }

                break;
            case PlanningAction.Validate:
                break;
            default:
                errors.Add($"Unknown planning action '{decision.Action}'.");
                break;
        }

        return errors;
    }

    private async Task AdvanceAsync(
        LoopState state,
        CancellationToken cancellationToken)
    {
        await state.SaveAsync(PlanningLoopStatus.Running, null, increment: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Produces requested artifacts before patching. The synthetic plan is
    /// validated like any planner proposal; outputs surface as fresh
    /// revisions next iteration. Returns false when the run must terminate.
    /// </summary>
    private async Task<bool> ProduceStagesAsync(
        LoopState state,
        string goal,
        PlanningDecision decision,
        int iteration,
        CancellationToken cancellationToken)
    {
        if (stageRunner is null)
        {
            return state.Fail("The decision requests artifact production, but no stage runner is configured.");
        }

        var plan = new PlanningStagePlan
        {
            Stages = decision.ProduceStages,
            Rationale = decision.Rationale,
            Provenance = new PlanningStagePlanProvenance
            {
                ProducedBy = $"planning-loop:{state.WorkflowId}#{iteration}",
                InputRevisions = state.KnownRevisions
                    .OrderBy(revision => revision, StringComparer.Ordinal).ToArray(),
                DefinitionCatalogueVersion = stageRunner.Catalogue.Version,
            },
        };
        var produced = await stageRunner.RunAsync(
            state.WorkflowId, plan, state.KnownRevisions, goal,
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!produced.Succeeded)
        {
            return state.Fail(
                "Artifact production failed: " + string.Join(" ", produced.Diagnostics));
        }

        return true;
    }

    /// <summary>
    /// Runs propose -> apply -> author -> preserve -> execute for one
    /// structural decision. Returns false when the run must terminate; the
    /// reason is carried by <see cref="LoopState.PendingFailure"/> or
    /// <see cref="LoopState.TerminalOverride"/>.
    /// </summary>
    private async Task<bool> ExpandAsync(
        LoopState state,
        string goal,
        PlanningDecision decision,
        ITrustedCatalogue catalogue,
        string catalogueSummary,
        string model,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        if (state.ModelCalls >= policy.MaxModelCalls)
        {
            return state.Fail("Max model calls reached before proposing.", isExhaustion: true);
        }

        var proposal = await proposer.ProposeAsync(
            goal,
            state.Current,
            PlannedExecutionDesignSummary.Render(state.Current),
            decision.MotivatingArtifacts,
            catalogueSummary,
            model,
            maxTokens,
            catalogue as ITrustedCatalogueDiscovery,
            cancellationToken).ConfigureAwait(false);
        state.ModelCalls += proposal.Attempts.Count;
        if (!proposal.Succeeded || proposal.Applied is null || proposal.Patch is null)
        {
            return state.Fail(
                "Patch proposal failed: " + string.Join("; ", proposal.Diagnostics));
        }

        var applied = proposal.Applied;
        if (applied.Graph.Steps.Count > policy.MaxWorkflowNodes)
        {
            return state.Fail(
                $"Merged design has {applied.Graph.Steps.Count} steps " +
                $"above MaxWorkflowNodes ({policy.MaxWorkflowNodes}).",
                isExhaustion: true);
        }

        if (string.Equals(
                StagedExecutionDesignIdentity.Compute(applied),
                state.Fingerprint,
                StringComparison.Ordinal))
        {
            state.ConsecutiveNoOps++;
            await AdvanceAsync(state, cancellationToken).ConfigureAwait(false);
            if (state.ConsecutiveNoOps >= policy.MaxConsecutiveNoOps)
            {
                state.TerminalOverride = (
                    PlanningLoopStatus.Finished,
                    $"No-op convergence after {state.ConsecutiveNoOps} consecutive no-change revisions.");
            }

            return state.TerminalOverride is null;
        }

        state.ConsecutiveNoOps = 0;
        if (state.Mutations >= policy.MaxMutations)
        {
            return state.Fail(
                $"Max mutations ({policy.MaxMutations}) reached.",
                isExhaustion: true);
        }

        if (state.ModelCalls >= policy.MaxModelCalls)
        {
            return state.Fail(
                $"Max model calls ({policy.MaxModelCalls}) reached before authoring.",
                isExhaustion: true);
        }

        var authored = await author.AuthorFromPatchAsync(
            goal,
            PlannedExecutionDesignSummary.Render(applied),
            state.Dsl,
            state.Plan,
            proposal.Patch,
            catalogueSummary,
            catalogue,
            model,
            maxTokens,
            cancellationToken).ConfigureAwait(false);
        state.ModelCalls += authored.Attempts.Count;
        if (!authored.Succeeded || authored.Admission?.Compilation.Definition is null)
        {
            return state.Fail(
                "Revision authoring failed: " + string.Join("; ", authored.Diagnostics));
        }

        var executed = await executionHost.ExecuteRevisionAsync(
            state.WorkflowId, applied, authored.Dsl, proposal.Patch, cancellationToken)
            .ConfigureAwait(false);
        state.Mutations++;
        state.StructuralIterations++;
        if (executed.ReportedTokens is not null)
        {
            state.ReportedTokens = (state.ReportedTokens ?? 0) + executed.ReportedTokens.Value;
        }

        state.Current = applied;
        state.Dsl = authored.Dsl;
        state.Plan = authored.Admission.Compilation.Definition.ReadPlan();
        state.WorkflowVersion = executed.WorkflowVersion;
        state.Evidence = executed.EvidenceSummary;
        await AdvanceAsync(state, cancellationToken).ConfigureAwait(false);

        if (policy.MaxTotalTokens is not null &&
            state.ReportedTokens is not null &&
            state.ReportedTokens >= policy.MaxTotalTokens)
        {
            return state.Fail(
                $"Max total tokens ({policy.MaxTotalTokens}) reached.",
                isExhaustion: true);
        }

        return true;
    }

    private async Task<PlanningLoopOutcome> FinishAsync(
        LoopState state,
        PlanningLoopStatus status,
        string reason,
        CancellationToken cancellationToken)
    {
        var checkpoint = await state.SaveAsync(status, reason, increment: status == PlanningLoopStatus.Running, cancellationToken)
            .ConfigureAwait(false);
        return new PlanningLoopOutcome(status, reason, checkpoint, state.Current);
    }

    private static async Task<WorkflowPlan?> CompileAsync(
        ITrustedCatalogue catalogue,
        string dsl,
        CancellationToken cancellationToken)
    {
        var compiled = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(dsl, cancellationToken: cancellationToken).ConfigureAwait(false);
        return compiled.Succeeded ? compiled.Plan : null;
    }

    private sealed record Restored(LoopState? State, PlanningLoopOutcome? Terminal);

    /// <summary>Mutable working state for one run; checkpoints persist it.</summary>
    private sealed class LoopState
    {
        public static LoopState CreateFresh(
            string workflowId,
            PlannedExecutionDesign initial,
            string dsl,
            WorkflowPlan plan,
            string workflowVersion,
            bool workflowComplete,
            string evidence,
            IPlanningArtifactCatalog catalog,
            PlanningPolicy policy)
        {
            var fingerprint = StagedExecutionDesignIdentity.Compute(initial);
            var state = new LoopState(catalog)
            {
                WorkflowId = workflowId,
                Current = initial,
                Dsl = dsl,
                Plan = plan,
                WorkflowVersion = workflowVersion,
                WorkflowComplete = workflowComplete,
                Evidence = evidence,
                Fingerprint = fingerprint,
                Checkpoint = new PlanningCheckpoint
                {
                    Iteration = 0,
                    WorkflowId = workflowId,
                    WorkflowVersion = workflowVersion,
                    DesignFingerprint = fingerprint,
                    Design = initial,
                    Dsl = dsl,
                    ModelCalls = 0,
                    Mutations = 0,
                    StructuralIterations = 0,
                    ReportedTokens = null,
                    ConsecutiveNoOps = 0,
                    KnownArtifactRevisions = [],
                    DecisionLog = [],
                    Status = PlanningLoopStatus.Running,
                    TerminalReason = null,
                },
            };
            return state;
        }

        public static LoopState Resume(
            PlanningCheckpoint checkpoint,
            WorkflowPlan plan,
            WorkflowExecutionSnapshot live,
            IPlanningArtifactCatalog catalog,
            PlanningPolicy policy)
        {
            var state = new LoopState(catalog)
            {
                WorkflowId = checkpoint.WorkflowId,
                Current = checkpoint.Design,
                Dsl = checkpoint.Dsl,
                Plan = plan,
                WorkflowVersion = live.WorkflowVersion,
                WorkflowComplete = live.IsComplete,
                Evidence = live.EvidenceSummary,
                Fingerprint = checkpoint.DesignFingerprint,
                ModelCalls = checkpoint.ModelCalls,
                Mutations = checkpoint.Mutations,
                StructuralIterations = checkpoint.StructuralIterations,
                ReportedTokens = checkpoint.ReportedTokens,
                ConsecutiveNoOps = checkpoint.ConsecutiveNoOps,
                Checkpoint = checkpoint,
            };
            foreach (var revision in checkpoint.KnownArtifactRevisions)
            {
                state.KnownRevisions.Add(revision);
            }

            return state;
        }

        private LoopState(IPlanningArtifactCatalog catalog)
        {
            Catalog = catalog;
        }

        public string WorkflowId { get; init; } = string.Empty;
        public PlannedExecutionDesign Current { get; set; } = null!;
        public string Dsl { get; set; } = null!;
        public WorkflowPlan Plan { get; set; } = null!;
        public string WorkflowVersion { get; set; } = null!;
        public bool WorkflowComplete { get; set; }
        public string Evidence { get; set; } = null!;
        public string Fingerprint { get; set; } = null!;
        public int ModelCalls { get; set; }
        public int Mutations { get; set; }
        public int StructuralIterations { get; set; }
        public int? ReportedTokens { get; set; }
        public int ConsecutiveNoOps { get; set; }
        public HashSet<string> KnownRevisions { get; } = new(StringComparer.Ordinal);
        public List<string> DecisionLog { get; } = [];
        public PlanningCheckpoint Checkpoint { get; set; } = null!;
        public string? PendingFailure { get; set; }
        public bool PendingFailureIsExhaustion { get; set; }
        public (PlanningLoopStatus Status, string Reason)? TerminalOverride { get; set; }

        private IPlanningArtifactCatalog Catalog { get; }

        public void Log(string entry) => DecisionLog.Add(entry);

        public bool Fail(string reason, bool isExhaustion = false)
        {
            PendingFailure = reason;
            PendingFailureIsExhaustion = isExhaustion;
            return false;
        }

        public async Task<PlanningCheckpoint> SaveAsync(
            PlanningLoopStatus status,
            string? reason,
            bool increment,
            CancellationToken cancellationToken)
        {
            Fingerprint = StagedExecutionDesignIdentity.Compute(Current);
            Checkpoint = new PlanningCheckpoint
            {
                Iteration = Checkpoint.Iteration + (increment ? 1 : 0),
                WorkflowId = WorkflowId,
                WorkflowVersion = WorkflowVersion,
                DesignFingerprint = Fingerprint,
                Design = Current,
                Dsl = Dsl,
                ModelCalls = ModelCalls,
                Mutations = Mutations,
                StructuralIterations = StructuralIterations,
                ReportedTokens = ReportedTokens,
                ConsecutiveNoOps = ConsecutiveNoOps,
                KnownArtifactRevisions = KnownRevisions
                    .OrderBy(revision => revision, StringComparer.Ordinal).ToArray(),
                DecisionLog = DecisionLog.ToArray(),
                Status = status,
                TerminalReason = reason,
            };
            await Catalog.PublishAsync(
                new PublishPlanningArtifactRequest<PlanningCheckpoint>(
                    WorkflowId,
                    new PlanningArtifactKey(CheckpointKind, WorkflowId),
                    CheckpointSchemaVersion,
                    ProducedBy,
                    Checkpoint,
                    State: PlanningArtifactState.Valid),
                cancellationToken).ConfigureAwait(false);
            return Checkpoint;
        }
    }
}
