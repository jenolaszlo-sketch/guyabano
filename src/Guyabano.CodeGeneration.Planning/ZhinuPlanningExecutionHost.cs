using System.Collections.Concurrent;
using System.Text.Json;
using Guyabano.Artifacts;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Zhinu-backed planning execution: compiles and admits Fuwen revisions,
/// registers versioned definitions, forks the live run at the earliest
/// affected node, executes, and reports evidence. Step reuse across versions
/// flows through prior-execution fingerprints (gap R28); the interpreter
/// supersedes steps whose contracts changed.
/// </summary>
/// <remarks>
/// Engine, store, registry, and definition-store lifetimes belong to the
/// caller. Run tracking is process-local: after a restart the host
/// recovers the latest run per workflow from the store, but the prior
/// execution fingerprint is then unknown and reuse degrades to
/// fingerprint-identical steps only.
/// </remarks>
public sealed class ZhinuPlanningExecutionHost(
    WorkflowEngine engine,
    WorkflowRegistry registry,
    IWorkflowDefinitionStore definitionStore,
    IActivityExecutor activityExecutor,
    IContextProvider contextProvider,
    IInferenceExecutor inferenceExecutor,
    ITrustedCatalogue catalogue) : IPlanningExecutionHost
{
    private const int EvidenceMaxCharacters = 1500;

    private readonly ConcurrentDictionary<string, TrackedRun> _runs = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates, registers, starts, and fully executes the initial workflow
    /// revision. Idempotent: returns the current snapshot when tracked.
    /// </summary>
    public async Task<WorkflowExecutionSnapshot> EnsureWorkflowAsync(
        string workflowId,
        string dsl,
        string inputJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dsl);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJson);

        if (_runs.ContainsKey(workflowId))
        {
            return await ObserveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        }

        var admission = await AdmitAsync(dsl, cancellationToken).ConfigureAwait(false);
        var ports = new FuwenZhinuExecutionPorts(activityExecutor, contextProvider, inferenceExecutor);
        var registration = await new FuwenZhinuWorkflowFactory(definitionStore, Identity(admission), ports)
            .CreateAsync(workflowId, "1", admission, cancellationToken).ConfigureAwait(false);
        registration.Register(registry);

        using var input = JsonDocument.Parse(inputJson);
        var runId = await engine.StartAsync(
            workflowId, "1", input.RootElement.Clone(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await engine.ExecuteAsync(runId, cancellationToken).ConfigureAwait(false);
        await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var run = await engine.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{runId}' vanished after execution.");
        if (run.Status != WorkflowStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Initial workflow revision failed with status {run.Status}: {RunError(run)}");
        }

        _runs[workflowId] = new TrackedRun(
            "1", runId, admission.Receipt!.ExecutionFingerprint);
        return Snapshot(run);
    }

    /// <summary>
    /// Re-admits and registers one revision without running it. Deterministic
    /// admission reproduces the receipt fingerprint, so cross-restart recovery
    /// restores full fingerprint reuse from the DSL alone.
    /// </summary>
    public async Task RegisterAsync(
        string workflowId,
        string version,
        string dsl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(dsl);

        var tracked = await ResolveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var admission = await AdmitAsync(dsl, cancellationToken).ConfigureAwait(false);
        var ports = new FuwenZhinuExecutionPorts(activityExecutor, contextProvider, inferenceExecutor);
        var registration = await new FuwenZhinuWorkflowFactory(definitionStore, Identity(admission), ports)
            .CreateAsync(workflowId, version, admission, cancellationToken).ConfigureAwait(false);
        registration.Register(registry);
        _runs[workflowId] = tracked with { ExecutionFingerprint = admission.Receipt!.ExecutionFingerprint };
    }

    /// <inheritdoc />
    public async Task<WorkflowExecutionSnapshot> ObserveAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        var tracked = await ResolveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var run = await engine.GetRunAsync(tracked.RunId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{tracked.RunId}' no longer exists.");
        return Snapshot(run);
    }

    /// <inheritdoc />
    public async Task<RevisionExecutionResult> ExecuteRevisionAsync(
        string workflowId,
        string dsl,
        WorkflowPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dsl);
        ArgumentNullException.ThrowIfNull(patch);

        var tracked = await ResolveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var admission = await AdmitAsync(dsl, cancellationToken).ConfigureAwait(false);
        var candidate = admission.Compilation.Definition?.ReadPlan()
            ?? throw new InvalidOperationException("Admitted revision has no readable plan.");
        if (string.Equals(
                WorkflowPlanIdentity.ComputeExecutionFingerprint(candidate),
                tracked.ExecutionFingerprint,
                StringComparison.Ordinal))
        {
            var idle = string.Join(",", patch.AffectedStepIds()
                .OrderBy(id => id, StringComparer.Ordinal));
            return new RevisionExecutionResult(
                tracked.Version,
                $"v{tracked.Version}: no executable change from patch [{idle}]; version unchanged",
                null);
        }

        var version = Increment(tracked.Version);
        var priors = await AncestorFingerprintsAsync(tracked.RunId, cancellationToken)
            .ConfigureAwait(false);
        priors.Add(tracked.ExecutionFingerprint);
        var ports = new FuwenZhinuExecutionPorts(activityExecutor, contextProvider, inferenceExecutor)
        {
            PriorExecutionFingerprints = priors,
        };
        var registration = await new FuwenZhinuWorkflowFactory(definitionStore, Identity(admission), ports)
            .CreateAsync(workflowId, version, admission, cancellationToken).ConfigureAwait(false);
        registration.Register(registry);

        var forkPoint = ForkPoint(candidate, patch);
        var runId = await engine.ForkAsync(
            tracked.RunId,
            forkPoint,
            new ForkRunOptions
            {
                TargetWorkflowVersion = version,
                Actor = "planning-loop",
                Reason = Truncate(patch.Rationale, 200),
            },
            cancellationToken).ConfigureAwait(false);
        await engine.ExecuteAsync(runId, cancellationToken).ConfigureAwait(false);
        await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var run = await engine.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{runId}' vanished after execution.");
        if (run.Status != WorkflowStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Revision {version} failed with status {run.Status}: {RunError(run)}");
        }

        _runs[workflowId] = new TrackedRun(
            version, runId, admission.Receipt!.ExecutionFingerprint);
        var affected = string.Join(",", patch.AffectedStepIds()
            .OrderBy(id => id, StringComparer.Ordinal));
        return new RevisionExecutionResult(
            version,
            $"v{version}: [{affected}] forked at '{forkPoint}'; {EvidenceBody(run)}",
            null);
    }

    private async Task<TrackedRun> ResolveAsync(
        string workflowId, CancellationToken cancellationToken)
    {
        if (_runs.TryGetValue(workflowId, out var tracked))
        {
            return tracked;
        }

        var runs = await engine.GetRunsAsync(
            new RunQuery { WorkflowName = workflowId, Limit = 1000 }, cancellationToken)
            .ConfigureAwait(false);
        var latest = runs
            .OrderByDescending(run => run.CreatedAt)
            .ThenByDescending(run => run.Id)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Unknown workflow '{workflowId}'; ensure it before observing.");
        var recovered = new TrackedRun(latest.WorkflowVersion, latest.Id, string.Empty);
        _runs[workflowId] = recovered;
        return recovered;
    }

    private async Task<WorkflowAdmissionResult> AdmitAsync(
        string dsl, CancellationToken cancellationToken)
    {
        var compiled = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(dsl, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!compiled.Succeeded || compiled.Plan is null)
        {
            throw new InvalidOperationException(
                "Revision does not compile: " + string.Join("; ", compiled.Diagnostics
                    .Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        }

        var admission = await new WorkflowAdmissionService(
                new WorkflowCompiler(
                    catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(compiled.Plan, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!admission.Succeeded || admission.Receipt is null)
        {
            throw new InvalidOperationException(
                "Revision is not admissible: " + string.Join("; ", admission.Diagnostics
                    .Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        }

        return admission;
    }

    private static FuwenZhinuProviderRuntimeIdentity Identity(WorkflowAdmissionResult admission) =>
        new(
            admission.Receipt!.CatalogueSnapshotRevision,
            admission.Receipt.ResolvedDescriptorSetFingerprint);

    private async Task<HashSet<string>> AncestorFingerprintsAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var current = await engine.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        while (current?.SourceRunId is Guid source)
        {
            var parent = await engine.GetRunAsync(source, cancellationToken).ConfigureAwait(false);
            if (parent?.DefinitionFingerprint is string fingerprint)
            {
                fingerprints.Add(fingerprint);
            }

            current = parent;
        }

        return fingerprints;
    }

    private static string Increment(string version)
    {
        if (!int.TryParse(
                version,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number) ||
            number < 0)
        {
            throw new InvalidOperationException(
                $"Workflow version '{version}' is not loop-managed; cannot derive the next revision.");
        }

        return (number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Earliest surviving affected node in execution order; the workflow
    /// root path when the patch only removes. Fingerprint reuse preserves
    /// everything outside the restart boundary.
    /// </summary>
    private static string ForkPoint(WorkflowPlan plan, WorkflowPatch patch)
    {
        var surviving = patch.AffectedStepIds()
            .Except(patch.RemoveStepIds, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var nodes = Flatten(plan.Nodes).ToList();
        var order = plan.ExecutionOrder?.Regions
            .SelectMany(region => region.Phases)
            .SelectMany(phase => phase.NodePaths)
            .ToArray() ?? nodes.Select(node => node.StructuralPath).ToArray();
        foreach (var path in order)
        {
            var node = nodes.SingleOrDefault(item =>
                string.Equals(item.StructuralPath, path, StringComparison.Ordinal));
            if (node is not null && surviving.Contains(node.Name))
            {
                return path;
            }
        }

        var first = order.FirstOrDefault() ?? nodes.FirstOrDefault()?.StructuralPath;
        if (first is null)
        {
            throw new InvalidOperationException("Candidate plan has no executable nodes.");
        }

        return first;
    }

    private static IEnumerable<WorkflowNode> Flatten(IEnumerable<WorkflowNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node is ConditionalNode conditional)
            {
                foreach (var child in Flatten(conditional.Then.Concat(conditional.Else)))
                    yield return child;
            }
            else if (node is FanOutNode fanOut)
            {
                foreach (var child in Flatten(fanOut.Body))
                    yield return child;
            }
            else if (node is RepeatNode repeat)
            {
                foreach (var child in Flatten(repeat.Body))
                    yield return child;
            }
        }
    }

    private static WorkflowExecutionSnapshot Snapshot(WorkflowRun run) =>
        new(run.WorkflowVersion, run.Status == WorkflowStatus.Completed, EvidenceBody(run));

    private static string EvidenceBody(WorkflowRun run)
    {
        if (run.Status == WorkflowStatus.Completed)
        {
            return $"completed; output: {Truncate(run.OutputJson ?? "null", EvidenceMaxCharacters)}";
        }

        if (run.Status == WorkflowStatus.Failed)
        {
            return $"failed: {RunError(run)}";
        }

        return $"incomplete ({run.Status})";
    }

    private static string RunError(WorkflowRun run) =>
        Truncate(run.Error?.ToString() ?? run.OutputJson ?? "no detail", 500);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…[truncated]";

    private sealed record TrackedRun(string Version, Guid RunId, string ExecutionFingerprint);
}
