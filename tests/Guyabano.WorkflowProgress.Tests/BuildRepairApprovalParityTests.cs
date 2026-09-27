using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Delivery F Wave 3: build-repair loop and approval-gate parity.
///
/// The repair DECISION logic (`CodeGenerationBuildRepairPlanner`) is covered
/// by dedicated planner unit tests; this wave proves the Fuwen-executed
/// cycle: a bounded repeat runs build, observes every build output through
/// the trusted repair activity in order, and breaks on clean state. Repair
/// routing lives in the trusted activity (host policy), not in conditional
/// Fuwen routing: branch bodies are closed regions and cannot observe sibling
/// outputs. Persistent dirt exhausts the bound; approval gates suspend on a
/// wait node and route accept/reject branches on the signal value. The corpus
/// records attempt counts, repair inputs, gate behavior, and run outcomes.
/// </summary>
public sealed class BuildRepairApprovalParityTests
{
    [Fact]
    public async Task BuildRepairLoop_SucceedsOnThirdAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var build = new ScriptedBuildActivity(["broken:err1", "broken:err2", "ok"]);
        var repair = new RecordingRepairActivity();
        await using var run = await ParityRun.StartAsync(
            BuildRepairPlan(), LoopCatalogue(), Ports(new LoopActivityRouter(build, repair)),
            "parity.buildrepair", "\"go\"", ct);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var output = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);

        output.GetString().Should().Be("ok");
        build.Calls.Should().HaveCount(3);
        repair.Inputs.Should().Equal("broken:err1", "broken:err2", "ok");
    }

    [Fact]
    public async Task BuildRepairLoop_ExhaustsBoundOnPersistentDirt()
    {
        var ct = TestContext.Current.CancellationToken;
        var build = new ScriptedBuildActivity(["broken", "broken", "broken", "broken"]);
        var repair = new RecordingRepairActivity();
        await using var run = await ParityRun.StartAsync(
            BuildRepairPlan(), LoopCatalogue(), Ports(new LoopActivityRouter(build, repair)),
            "parity.buildrepair", "\"go\"", ct);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var error = await Assert.ThrowsAsync<WorkflowExecutionFailedException>(() =>
            run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct));

        build.Calls.Should().HaveCount(3);
        repair.Inputs.Should().Equal("broken", "broken", "broken");
        error.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ApprovalGate_ApproveProceeds()
    {
        var (output, calls) = await RunApprovalAsync("approved");

        output.Should().Be("proceeded");
        calls.Should().Contain("parity.proceed");
        calls.Should().NotContain("parity.reject");
    }

    [Fact]
    public async Task ApprovalGate_RejectTakesRejectPath()
    {
        var (output, calls) = await RunApprovalAsync("rejected");

        output.Should().Be("rejected");
        calls.Should().Contain("parity.reject");
        calls.Should().NotContain("parity.proceed");
    }

    private static async Task<(string Output, List<string> Calls)> RunApprovalAsync(string verdict)
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new ScriptedGateActivity();
        var catalogue = GateCatalogue();
        var admission = await new WorkflowAdmissionService(
                new WorkflowCompiler(catalogue,
                    capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(BuildApprovalPlan(), cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Succeeded)
            throw new InvalidOperationException(
                $"Admission failed: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}"))}");
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("parity.approval", "1", admission, ct)
            .ConfigureAwait(false);
        var root = Path.Combine(
            Path.GetTempPath(), "guyabano-approval-parity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(store,
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var input = JsonDocument.Parse("\"request\"");
            var runId = await engine.StartAsync("parity.approval", "1", input.RootElement.Clone(),
                cancellationToken: ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var execution = engine.ExecuteAsync(runId, cts.Token);

            while (true)
            {
                var steps = await engine.GetStepsAsync(runId, cts.Token).ConfigureAwait(false);
                if (steps.Any(s => s.StepKey.EndsWith("/gate") && s.Status == StepStatus.Waiting))
                    break;
                if (steps.All(s => s.Status is StepStatus.Completed or StepStatus.Failed))
                    break;
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }

            await engine.SendSignalAsync(runId, "approval-signal", verdict, cts.Token).ConfigureAwait(false);
            await execution.ConfigureAwait(false);

            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)
                .ConfigureAwait(false);
            return (output.GetString()!, activity.Calls);
        }
        finally
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch { await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false); }
            }
        }
    }

    private static readonly DescriptorReference BuildDescriptor = new(
        DescriptorKind.Activity, "parity.build", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('b', 64)));

    private static readonly DescriptorReference RepairDescriptor = new(
        DescriptorKind.Activity, "parity.repair", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('c', 64)));

    private static readonly DescriptorReference ProceedDescriptor = new(
        DescriptorKind.Activity, "parity.proceed", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('e', 64)));

    private static readonly DescriptorReference RejectDescriptor = new(
        DescriptorKind.Activity, "parity.reject", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('f', 64)));

    private static PrimitiveType Str => new(FuwenPrimitiveKind.String);

    private static CallableContract StringContract(string parameter) => new(
        new CallableSignature([new CallableParameter(parameter, Str)], Str),
        CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);

    private static ITrustedCatalogue LoopCatalogue() => new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(BuildDescriptor, callableContract: StringContract("value")),
        new TrustedCatalogueDescriptor(RepairDescriptor, callableContract: StringContract("value")),
    ]);

    private static ITrustedCatalogue GateCatalogue() => new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(ProceedDescriptor, callableContract: StringContract("value")),
        new TrustedCatalogueDescriptor(RejectDescriptor, callableContract: StringContract("value")),
    ]);

    private static FuwenZhinuExecutionPorts Ports(IActivityExecutor activity) =>
        new(activity, new UnusedContext(), new UnusedInference(),
            observer: null,
            new FuwenZhinuExecutionPorts.Options(
                maximumInfrastructureAttempts: 1, maximumFanOutConcurrency: 2));

    private static WorkflowPlan BuildRepairPlan()
    {
        // Repair routing lives in the trusted repair activity (host policy):
        // it observes every build output in order and passes it through. The
        // loop breaks on clean state; persistent dirt exhausts the bound.
        var loopPath = StructuralNodeIdentity.Create("parityBuildRepair", "buildLoop");
        var buildPath = loopPath + "/$body/buildAttempt";
        var repairPath = loopPath + "/$body/repair";
        var returnPath = StructuralNodeIdentity.Create("parityBuildRepair", "return_result");
        return new WorkflowPlanBuilder("parityBuildRepair", "1", Str, Str, "routing/1")
            .AddNode(new RepeatNode(
                "buildLoop", loopPath,
                3, Str,
                new InputBinding([]),
                [
                    new ActivityNode("buildAttempt", buildPath, BuildDescriptor,
                        [new ArgumentBinding("value", new LoopStateBinding([]))], Str),
                    new ActivityNode("repair", repairPath, RepairDescriptor,
                        [new ArgumentBinding("value", new NodeOutputBinding(buildPath, []))], Str),
                ],
                new NodeOutputBinding(repairPath, []),
                new ConditionExpression(ConditionOperator.Equal,
                    new LoopStateBinding([]),
                    new LiteralBinding(JsonDocument.Parse("\"ok\"").RootElement.Clone())),
                Str))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(loopPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("parityBuildRepair", [
                    new WorkflowExecutionPhase([loopPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{loopPath}/$body", [
                    new WorkflowExecutionPhase([buildPath]),
                    new WorkflowExecutionPhase([repairPath]),
                ]),
            ]))
            .Build();
    }

    private static WorkflowPlan BuildApprovalPlan()
    {
        var waitPath = StructuralNodeIdentity.Create("parityApproval", "gate");
        var condPath = StructuralNodeIdentity.Create("parityApproval", "decision");
        var proceedPath = condPath + "/$then/proceed";
        var rejectPath = condPath + "/$else/reject";
        var returnPath = StructuralNodeIdentity.Create("parityApproval", "return_result");
        return new WorkflowPlanBuilder("parityApproval", "1", Str, Str, "routing/1")
            .AddNode(new WaitNode("gate", waitPath, "approval-signal", Str, TimeoutSeconds: 300))
            .AddNode(new ConditionalNode(
                "decision", condPath,
                new ConditionExpression(ConditionOperator.Equal,
                    new NodeOutputBinding(waitPath, []),
                    new LiteralBinding(JsonDocument.Parse("\"approved\"").RootElement.Clone())),
                [new ActivityNode("proceed", proceedPath, ProceedDescriptor,
                    [new ArgumentBinding("value", new InputBinding([]))], Str)],
                [new ActivityNode("reject", rejectPath, RejectDescriptor,
                    [new ArgumentBinding("value", new InputBinding([]))], Str)],
                new ConditionalMerge(
                    new NodeOutputBinding(proceedPath, []),
                    new NodeOutputBinding(rejectPath, []),
                    Str)))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(condPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("parityApproval", [
                    new WorkflowExecutionPhase([waitPath]),
                    new WorkflowExecutionPhase([condPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{condPath}/$then", [
                    new WorkflowExecutionPhase([proceedPath]),
                ]),
                new WorkflowExecutionRegion($"{condPath}/$else", [
                    new WorkflowExecutionPhase([rejectPath]),
                ]),
            ]))
            .Build();
    }

    private sealed class ParityRun : IAsyncDisposable
    {
        public WorkflowEngine Engine { get; }
        public Guid RunId { get; }

        private readonly string _root;

        private ParityRun(string root, WorkflowEngine engine, Guid runId)
        {
            _root = root;
            Engine = engine;
            RunId = runId;
        }

        public static async Task<ParityRun> StartAsync(
            WorkflowPlan plan,
            ITrustedCatalogue catalogue,
            FuwenZhinuExecutionPorts ports,
            string workflowName,
            string inputJson,
            CancellationToken ct)
        {
            var admission = await new WorkflowAdmissionService(
                    new WorkflowCompiler(catalogue,
                        capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
                .AdmitAsync(plan, cancellationToken: ct)
                .ConfigureAwait(false);
            if (!admission.Succeeded)
                throw new InvalidOperationException(
                    $"Admission failed: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path} expected={d.Expected} actual={d.Actual}"))}");
            var registration = await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    new FuwenZhinuProviderRuntimeIdentity(
                        admission.Receipt!.CatalogueSnapshotRevision,
                        admission.Receipt.ResolvedDescriptorSetFingerprint),
                    ports)
                .CreateAsync(workflowName, "1", admission, ct)
                .ConfigureAwait(false);
            var root = Path.Combine(
                Path.GetTempPath(), "guyabano-buildrepair-parity", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            var engine = new WorkflowEngine(store,
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var input = JsonDocument.Parse(inputJson);
            var runId = await engine.StartAsync(workflowName, "1", input.RootElement.Clone(),
                cancellationToken: ct).ConfigureAwait(false);
            return new ParityRun(root, engine, runId);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync().ConfigureAwait(false);
            for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
            {
                try { Directory.Delete(_root, true); break; }
                catch { await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false); }
            }
        }
    }

    private sealed class LoopActivityRouter : IActivityExecutor
    {
        private readonly ScriptedBuildActivity _build;
        private readonly RecordingRepairActivity _repair;

        public LoopActivityRouter(ScriptedBuildActivity build, RecordingRepairActivity repair)
        {
            _build = build;
            _repair = repair;
        }

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default) =>
            request.Activity.Name switch
            {
                "parity.build" => _build.ExecuteAsync(request, ct),
                "parity.repair" => _repair.ExecuteAsync(request, ct),
                "parity.noop" => Noop(request),
                _ => throw new InvalidOperationException($"Unexpected activity '{request.Activity.Name}'."),
            };

        private static ValueTask<ActivityExecutionResult> Noop(ActivityExecutionRequest request)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input));
            return ValueTask.FromResult(
                ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private sealed class ScriptedBuildActivity : IActivityExecutor
    {
        private readonly Queue<string> _outputs;
        public readonly List<string> Calls = [];

        public ScriptedBuildActivity(IEnumerable<string> outputs) =>
            _outputs = new Queue<string>(outputs);

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            Calls.Add(input);
            var output = _outputs.Count > 0 ? _outputs.Dequeue() : "broken:exhausted";
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return ValueTask.FromResult(
                ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private sealed class RecordingRepairActivity : IActivityExecutor
    {
        public readonly List<string> Inputs = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            Inputs.Add(input);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input));
            return ValueTask.FromResult(
                ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private sealed class ScriptedGateActivity : IActivityExecutor
    {
        public readonly List<string> Calls = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default)
        {
            Calls.Add(request.Activity.Name);
            var output = request.Activity.Name switch
            {
                "parity.proceed" => "proceeded",
                "parity.reject" => "rejected",
                _ => throw new InvalidOperationException($"Unexpected activity '{request.Activity.Name}'."),
            };
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return ValueTask.FromResult(
                ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(
            ContextExecutionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("No context node exists in parity plans.");
    }

    private sealed class UnusedInference : IInferenceExecutor, IInferenceExecutorPreflight
    {
        public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement) => null;

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("No inference node exists in parity plans.");
    }
}

