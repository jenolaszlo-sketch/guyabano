using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Delivery F Wave 1: decomposition parity corpus.
///
/// The recorded-failure corpus lives here as checked-in deterministic
/// scenarios. Comparison dimensions for old-path removal:
/// outputs, diagnostics, repair/retry rate, provider call counts,
/// step-key provenance, and restart scope. Token use is provider-reported
/// and is covered by inference-bearing waves (Wave 2+); Wave 1a locks the
/// scheduling contract plus call/retry/provenance/restart/output behavior
/// for activity-only decomposition waves.
///
/// The scheduling contract (`OrderCodeGenerationTasks` /
/// `GetReadyCodeGenerationTasks`) is shared by the hard-coded wave loop and
/// any Fuwen consumer: the host computes ready lists, Fuwen fan-out consumes
/// a bounded list per wave. Part A locks those sequences; Part B proves the
/// Fuwen side executes them against scripted providers.
///
/// Open parity dimensions (required before old-path removal):
/// - Hard-coded step keys (`decomposition/{version}/{parent}`) versus Fuwen
///   content-hash runtime keys: the mapping rule must be recorded and the
///   recorded corpus must assert it per wave.
/// - Item failure surfaces at run level carrying the provider diagnostic, but
///   neither the run message nor the (still Completed) item steps name the
///   failing activity's structural path; the hard-coded path marks failed
///   steps explicitly. Decide which surface is authoritative for operator
///   diagnostics before removal.
/// - Live differential execution of the full hard-coded workflow against the
///   same scripts, plus the focused-restart dogfood run (Stage 5 waves 2-4).
/// </summary>
public sealed class DecompositionParityTests
{
    // ------------------------------------------------------------------
    // Part A: corpus scheduling contract (shared by both paths)
    // ------------------------------------------------------------------

    [Fact]
    public void DiamondDag_WavesAreTopologicallyOrdered()
    {
        var plan = DagPlan(
            ("scaffold", PlanTaskExecutionKind.Scaffolding, []),
            ("a", PlanTaskExecutionKind.CodeGeneration, []),
            ("b", PlanTaskExecutionKind.CodeGeneration, []),
            ("c", PlanTaskExecutionKind.CodeGeneration, ["a", "b"]),
            ("d", PlanTaskExecutionKind.CodeGeneration, ["c"]));

        var ordered = CodeGenerationWorkflow.OrderCodeGenerationTasks(plan);
        ordered.Select(task => task.Id).Should().Equal("a", "b", "c", "d");

        var scaffolding = plan.Tasks
            .Where(task => task.ExecutionKind == PlanTaskExecutionKind.Scaffolding)
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);
        WaveIds(plan, scaffolding).Should().Equal("a", "b");
        WaveIds(plan, scaffolding.Union(["a", "b"]).ToHashSet(StringComparer.Ordinal))
            .Should().Equal("c");
        WaveIds(plan, scaffolding.Union(["a", "b", "c"]).ToHashSet(StringComparer.Ordinal))
            .Should().Equal("d");
        WaveIds(plan, scaffolding.Union(["a", "b", "c", "d"]).ToHashSet(StringComparer.Ordinal))
            .Should().BeEmpty();
    }

    [Fact]
    public void LinearChain_WavesAreSingleton()
    {
        var plan = DagPlan(
            ("a", PlanTaskExecutionKind.CodeGeneration, []),
            ("b", PlanTaskExecutionKind.CodeGeneration, ["a"]),
            ("c", PlanTaskExecutionKind.CodeGeneration, ["b"]));

        CodeGenerationWorkflow.OrderCodeGenerationTasks(plan)
            .Select(task => task.Id).Should().Equal("a", "b", "c");
        WaveIds(plan, new HashSet<string>(StringComparer.Ordinal)).Should().Equal("a");
        WaveIds(plan, new HashSet<string>(["a"], StringComparer.Ordinal)).Should().Equal("b");
        WaveIds(plan, new HashSet<string>(["a", "b"], StringComparer.Ordinal)).Should().Equal("c");
    }

    [Fact]
    public void IndependentFan_IsOneWave()
    {
        var plan = DagPlan(
            ("a", PlanTaskExecutionKind.CodeGeneration, []),
            ("b", PlanTaskExecutionKind.CodeGeneration, []),
            ("c", PlanTaskExecutionKind.CodeGeneration, []));

        CodeGenerationWorkflow.OrderCodeGenerationTasks(plan)
            .Select(task => task.Id).Should().Equal("a", "b", "c");
        WaveIds(plan, new HashSet<string>(StringComparer.Ordinal))
            .Should().Equal("a", "b", "c");
    }

    [Fact]
    public void ReadySet_ExcludesCompletedAndBlockedTasks()
    {
        var plan = DagPlan(
            ("a", PlanTaskExecutionKind.CodeGeneration, []),
            ("b", PlanTaskExecutionKind.CodeGeneration, ["a"]));

        CodeGenerationWorkflow.GetReadyCodeGenerationTasks(
                plan, new HashSet<string>(["a", "b"], StringComparer.Ordinal))
            .Should().BeEmpty();
        CodeGenerationWorkflow.GetReadyCodeGenerationTasks(
                plan, new HashSet<string>(["zzz"], StringComparer.Ordinal))
            .Select(task => task.Id).Should().Equal("a");
    }

    private static IReadOnlyList<string> WaveIds(
        CodeGenerationPlan plan, IReadOnlySet<string> completed) =>
        CodeGenerationWorkflow.GetReadyCodeGenerationTasks(plan, completed)
            .Select(task => task.Id).ToArray();

    private static CodeGenerationPlan DagPlan(
        params (string Id, PlanTaskExecutionKind Kind, string[] DependsOn)[] tasks) =>
        new()
        {
            Title = "parity",
            Summary = "parity",
            Assumptions = [],
            Solution = new PlannedSolution { Name = "Parity", Path = "Parity.sln" },
            Projects = [],
            Modules = [],
            Contracts = [],
            Decisions = [],
            ArchitectureNotes = [],
            AcceptanceCriteria = [],
            Tasks = tasks.Select(item => new GenerationTaskPlan
            {
                Id = item.Id,
                Title = item.Id,
                Objective = item.Id,
                ExecutionKind = item.Kind,
                ComplexityPoints = 1,
                ComplexityReasons = ["parity"],
                DecompositionRecommended = false,
                EstimatedFiles = 1,
                DependsOn = item.DependsOn.ToList(),
                ContractIds = [],
                Relationships = ComponentRelationshipPlan.Empty,
                DecisionIds = [],
                AcceptanceCriterionIds = [],
                Deliverables = [],
                VerificationKinds = []
            }).ToList()
        };

    // ------------------------------------------------------------------
    // Part B: scripted fan-out execution parity
    // ------------------------------------------------------------------

    [Fact]
    public async Task WaveExecutesInSourceOrderAndAggregates()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new ScriptedDecomposeActivity();
        await using var run = await DecompositionRun.StartAsync(activity, "[\"b\",\"a\",\"c\"]", ct);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var output = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);

        output.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("decomposed:b", "decomposed:a", "decomposed:c");
        activity.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task FailOnceThenSucceed_RetriesOnlyThatItem()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new ScriptedDecomposeActivity();
        activity.Script("b", ScriptedOutcome.TransientFailure(), ScriptedOutcome.Success());
        await using var run = await DecompositionRun.StartAsync(
            activity, "[\"a\",\"b\",\"c\"]", ct, maximumInfrastructureAttempts: 2);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var output = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);

        output.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("decomposed:a", "decomposed:b", "decomposed:c");
        activity.Calls.Should().HaveCount(4);
        activity.Calls.Count(call => call == "b").Should().Be(2);
        activity.Calls.Count(call => call == "a").Should().Be(1);
        activity.Calls.Count(call => call == "c").Should().Be(1);
    }

    [Fact]
    public async Task FatalFailure_FailsRunWithProvenance()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new ScriptedDecomposeActivity();
        activity.Script("b", ScriptedOutcome.FatalFailure());
        await using var run = await DecompositionRun.StartAsync(
            activity, "[\"a\",\"b\",\"c\"]", ct);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var error = await Assert.ThrowsAsync<WorkflowExecutionFailedException>(() =>
            run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct));

        // The run failure carries the provider diagnostic; every item was
        // attempted exactly once and no retry was issued. The failing
        // activity's structural path is not part of the run message (see the
        // open dimensions note above).
        error.Message.Should().Contain("fatal");
        activity.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task RestartMidWave_RerunsOnlyInvalidatedItem()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new ScriptedDecomposeActivity();
        await using var run = await DecompositionRun.StartAsync(
            activity, "[\"b\",\"a\",\"c\"]", ct);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var first = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);
        first.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("decomposed:b", "decomposed:a", "decomposed:c");
        activity.Calls.Should().HaveCount(3);

        var itemPath = RuntimeNodeIdentity.CreateFanOutItem(
            DecompositionRun.FanOutPath, new StringRuntimeKey("a"));
        var restart = await run.Engine.RestartStepAsync(run.RunId, itemPath, ct);
        restart.StepsToInvalidate.Select(s => s.StepKey).Should().Contain(itemPath);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var second = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);
        second.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("decomposed:b", "decomposed:a", "decomposed:c");
        activity.Calls.Should().HaveCount(4);
        activity.Calls.Count(call => call == "a").Should().Be(2);
    }

    private static readonly DescriptorReference DecomposeDescriptor = new(
        DescriptorKind.Activity, "parity.decompose", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('d', 64)));

    private static ITrustedCatalogue CreateCatalogue() => new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(DecomposeDescriptor, callableContract: new CallableContract(
            new CallableSignature(
                [new CallableParameter("task", new PrimitiveType(FuwenPrimitiveKind.String))],
                new PrimitiveType(FuwenPrimitiveKind.String)),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    ]);

    private static WorkflowPlan BuildDecomposePlan()
    {
        var itemType = new PrimitiveType(FuwenPrimitiveKind.String);
        var output = new ListType(itemType, 8);
        var fanOutPath = StructuralNodeIdentity.Create("parityDecompose", "decompose");
        var returnPath = StructuralNodeIdentity.Create("parityDecompose", "return_result");
        var bodyActivityPath = $"{fanOutPath}/$body/decompose";
        var fanOut = new FanOutNode(
            "decompose",
            fanOutPath,
            new InputBinding([]),
            new FanOutItemBinding("item", itemType),
            new FanOutItemValueBinding([]),
            new List<WorkflowNode>
            {
                new ActivityNode("decompose", bodyActivityPath, DecomposeDescriptor,
                    [new ArgumentBinding("task", new FanOutItemValueBinding([]))], itemType)
            },
            new NodeOutputBinding(bodyActivityPath, []),
            output,
            MaximumItems: 8,
            MaximumConcurrency: 4);
        return new WorkflowPlanBuilder("parityDecompose", "1", new ListType(itemType, 8), output, "routing/1")
            .AddFanOut(fanOut)
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(fanOutPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("parityDecompose", [
                    new WorkflowExecutionPhase([fanOutPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{fanOutPath}/$body", [
                    new WorkflowExecutionPhase([bodyActivityPath]),
                ]),
            ]))
            .Build();
    }

    private sealed class DecompositionRun : IAsyncDisposable
    {
        public static readonly string FanOutPath =
            StructuralNodeIdentity.Create("parityDecompose", "decompose");

        public string Root { get; }
        public WorkflowEngine Engine { get; }
        public Guid RunId { get; }

        private DecompositionRun(string root, WorkflowEngine engine, Guid runId)
        {
            Root = root;
            Engine = engine;
            RunId = runId;
        }

        public static async Task<DecompositionRun> StartAsync(
            IActivityExecutor activity,
            string inputJson,
            CancellationToken ct,
            int maximumInfrastructureAttempts = 1)
        {
            var catalogue = CreateCatalogue();
            var admission = await new WorkflowAdmissionService(
                    new WorkflowCompiler(catalogue,
                        capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
                .AdmitAsync(BuildDecomposePlan(), cancellationToken: ct)
                .ConfigureAwait(false);
            if (!admission.Succeeded)
                throw new InvalidOperationException(
                    $"Admission failed: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message}"))}");
            var registration = await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    new FuwenZhinuProviderRuntimeIdentity(
                        admission.Receipt!.CatalogueSnapshotRevision,
                        admission.Receipt.ResolvedDescriptorSetFingerprint),
                    new FuwenZhinuExecutionPorts(
                        activity, new UnusedContext(), new UnusedInference(),
                        observer: null,
                        new FuwenZhinuExecutionPorts.Options(
                            maximumInfrastructureAttempts, maximumFanOutConcurrency: 2)))
                .CreateAsync("parity.decompose", "1", admission, ct)
                .ConfigureAwait(false);
            var root = Path.Combine(
                Path.GetTempPath(), "guyabano-decomposition-parity", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var input = JsonDocument.Parse(inputJson);
            var runId = await engine.StartAsync("parity.decompose", "1", input.RootElement.Clone(),
                cancellationToken: ct).ConfigureAwait(false);
            return new DecompositionRun(root, engine, runId);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync().ConfigureAwait(false);
            for (var attempt = 0; attempt < 5 && Directory.Exists(Root); attempt++)
            {
                try { Directory.Delete(Root, true); break; }
                catch { await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false); }
            }
        }
    }

    private sealed class ScriptedDecomposeActivity : IActivityExecutor
    {
        private readonly Dictionary<string, Queue<ScriptedOutcome>> _scripts = new(StringComparer.Ordinal);
        public readonly List<string> Calls = [];

        public void Script(string item, params ScriptedOutcome[] outcomes) =>
            _scripts[item] = new Queue<ScriptedOutcome>(outcomes);

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var item = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            Calls.Add(item);
            if (_scripts.TryGetValue(item, out var queue) && queue.Count > 0)
                return ValueTask.FromResult(queue.Dequeue().Apply(item));
            return ValueTask.FromResult(ScriptedOutcome.Success().Apply(item));
        }
    }

    private sealed record ScriptedOutcome(bool Succeed, string? Output, bool Retryable)
    {
        public static ScriptedOutcome Success(string? output = null) => new(true, output, false);
        public static ScriptedOutcome TransientFailure() => new(false, "boom", true);
        public static ScriptedOutcome FatalFailure() => new(false, "fatal", false);

        public ActivityExecutionResult Apply(string item)
        {
            if (Succeed)
            {
                using var doc = JsonDocument.Parse(
                    JsonSerializer.Serialize(Output ?? "decomposed:" + item));
                return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement));
            }
            var failure = Retryable
                ? new ExecutionFailure(
                    ExecutionFailureKind.Infrastructure,
                    ExecutionFailureCode.TransientInfrastructureFailure,
                    Output!,
                    ExecutionRetryDisposition.InfrastructureOnly)
                : new ExecutionFailure(
                    ExecutionFailureKind.Provider,
                    ExecutionFailureCode.ProviderError,
                    Output!);
            return ActivityExecutionResult.Failed(failure);
        }
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(
            ContextExecutionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("No context node exists in decomposition parity plans.");
    }

    private sealed class UnusedInference : IInferenceExecutor, IInferenceExecutorPreflight
    {
        public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement) => null;

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("No inference node exists in decomposition parity plans.");
    }
}
