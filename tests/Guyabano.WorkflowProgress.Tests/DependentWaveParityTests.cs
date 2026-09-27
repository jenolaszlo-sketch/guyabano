#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Delivery F Wave 4: dependent waves and cross-pipeline restart scope.
///
/// Waves 1a-3 proved single fan-outs, loops, conditionals, and waits in
/// isolation. This wave proves composition: a second fan-out consuming the
/// first wave's outputs (dependent waves in one plan), and restart
/// invalidation across a prepare/fan-out/build pipeline. Together with the
/// dogfood protocol recorded in `docs/fuwen-pilot.md`, this completes the
/// automated evidence for the old-path removal decision.
/// </summary>
public sealed class DependentWaveParityTests
{
    [Fact]
    public async Task ChainedFanOut_WaveBConsumesWaveAOutputsInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new ScriptedWaveActivity();
        await using var run = await WaveRun.StartAsync(
            BuildChainedPlan(), ChainCatalogue(), Ports(activity),
            "parity.waves", "[\"x\",\"y\"]", ct);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var output = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);

        output.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("x:a:b", "y:a:b");
        activity.Calls("parity.stepA").Should().HaveCount(2);
        activity.Calls("parity.stepB").Should().HaveCount(2);
    }

    [Fact]
    public async Task RestartWaveBItem_DoesNotRerunWaveA()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new ScriptedWaveActivity();
        await using var run = await WaveRun.StartAsync(
            BuildChainedPlan(), ChainCatalogue(), Ports(activity),
            "parity.waves", "[\"x\",\"y\"]", ct);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct)
            .ConfigureAwait(false);

        var itemPath = RuntimeNodeIdentity.CreateFanOutItem(
            WaveRun.FanOutBPath, new StringRuntimeKey("x:a"));
        var restart = await run.Engine.RestartStepAsync(run.RunId, itemPath, ct);
        restart.StepsToInvalidate.Select(s => s.StepKey).Should().Contain(itemPath);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var output = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);
        output.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("x:a:b", "y:a:b");
        activity.Calls("parity.stepA").Should().HaveCount(2);
        activity.Calls("parity.stepB").Should().HaveCount(3);
    }

    [Fact]
    public async Task RestartBuild_RerunsBuildButNotFanOutItems()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new ScriptedWaveActivity();
        await using var run = await WaveRun.StartAsync(
            BuildPipelinePlan(), ChainCatalogue(), Ports(activity),
            "parity.pipeline", "[\"x\",\"y\"]", ct);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var first = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);
        first.GetString().Should().Be("built:2");
        activity.Calls("parity.stepA").Should().HaveCount(2);
        activity.Calls("parity.build").Should().HaveCount(1);

        var restart = await run.Engine.RestartStepAsync(run.RunId, WaveRun.BuildPath, ct);
        restart.StepsToInvalidate.Select(s => s.StepKey).Should().Contain(WaveRun.BuildPath);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var second = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);
        second.GetString().Should().Be("built:2");
        activity.Calls("parity.stepA").Should().HaveCount(2);
        activity.Calls("parity.build").Should().HaveCount(2);
    }

    private static readonly DescriptorReference StepADescriptor = new(
        DescriptorKind.Activity, "parity.stepA", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));

    private static readonly DescriptorReference StepBDescriptor = new(
        DescriptorKind.Activity, "parity.stepB", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('b', 64)));

    private static readonly DescriptorReference BuildDescriptor = new(
        DescriptorKind.Activity, "parity.build", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('c', 64)));
    private static PrimitiveType Str => new(FuwenPrimitiveKind.String);

    private static ITrustedCatalogue ChainCatalogue() => new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(StepADescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("task", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(StepBDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("task", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(BuildDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("value", new ListType(Str, 8))], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    ]);

    private static FuwenZhinuExecutionPorts Ports(IActivityExecutor activity) =>
        new(activity, new UnusedContext(), new UnusedInference(),
            observer: null,
            new FuwenZhinuExecutionPorts.Options(
                maximumInfrastructureAttempts: 1, maximumFanOutConcurrency: 2));

    private static WorkflowPlan BuildChainedPlan()
    {
        var itemType = Str;
        var output = new ListType(itemType, 8);
        var fanOutA = StructuralNodeIdentity.Create("parityWaves", "waveA");
        var bodyA = $"{fanOutA}/$body/stepA";
        var fanOutB = StructuralNodeIdentity.Create("parityWaves", "waveB");
        var bodyB = $"{fanOutB}/$body/stepB";
        var returnPath = StructuralNodeIdentity.Create("parityWaves", "return_result");
        return new WorkflowPlanBuilder("parityWaves", "1", new ListType(itemType, 8), output, "routing/1")
            .AddFanOut(new FanOutNode(
                "waveA",
                fanOutA,
                new InputBinding([]),
                new FanOutItemBinding("item", itemType),
                new FanOutItemValueBinding([]),
                new List<WorkflowNode>
                {
                    new ActivityNode("stepA", bodyA, StepADescriptor,
                        [new ArgumentBinding("task", new FanOutItemValueBinding([]))], itemType)
                },
                new NodeOutputBinding(bodyA, []),
                output,
                MaximumItems: 8,
                MaximumConcurrency: 4))
            .AddFanOut(new FanOutNode(
                "waveB",
                fanOutB,
                new NodeOutputBinding(fanOutA, []),
                new FanOutItemBinding("item", itemType),
                new FanOutItemValueBinding([]),
                new List<WorkflowNode>
                {
                    new ActivityNode("stepB", bodyB, StepBDescriptor,
                        [new ArgumentBinding("task", new FanOutItemValueBinding([]))], itemType)
                },
                new NodeOutputBinding(bodyB, []),
                output,
                MaximumItems: 8,
                MaximumConcurrency: 4))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(fanOutB, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("parityWaves", [
                    new WorkflowExecutionPhase([fanOutA]),
                    new WorkflowExecutionPhase([fanOutB]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{fanOutA}/$body", [
                    new WorkflowExecutionPhase([bodyA]),
                ]),
                new WorkflowExecutionRegion($"{fanOutB}/$body", [
                    new WorkflowExecutionPhase([bodyB]),
                ]),
            ]))
            .Build();
    }

    private static WorkflowPlan BuildPipelinePlan()
    {
        var fanOutPath = StructuralNodeIdentity.Create("parityPipeline", "items");
        var bodyPath = $"{fanOutPath}/$body/stepA";
        var buildPath = StructuralNodeIdentity.Create("parityPipeline", "build");
        var returnPath = StructuralNodeIdentity.Create("parityPipeline", "return_result");
        return new WorkflowPlanBuilder("parityPipeline", "1", new ListType(Str, 8), Str, "routing/1")
            .AddFanOut(new FanOutNode(
                "items",
                fanOutPath,
                new InputBinding([]),
                new FanOutItemBinding("item", Str),
                new FanOutItemValueBinding([]),
                new List<WorkflowNode>
                {
                    new ActivityNode("stepA", bodyPath, StepADescriptor,
                        [new ArgumentBinding("task", new FanOutItemValueBinding([]))], Str)
                },
                new NodeOutputBinding(bodyPath, []),
                new ListType(Str, 8),
                MaximumItems: 8,
                MaximumConcurrency: 4))
            .AddNode(new ActivityNode("build", buildPath, BuildDescriptor,
                [new ArgumentBinding("value", new NodeOutputBinding(fanOutPath, []))], Str))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(buildPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("parityPipeline", [
                    new WorkflowExecutionPhase([fanOutPath]),
                    new WorkflowExecutionPhase([buildPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{fanOutPath}/$body", [
                    new WorkflowExecutionPhase([bodyPath]),
                ]),
            ]))
            .Build();
    }

    private sealed class WaveRun : IAsyncDisposable
    {
        public static readonly string FanOutBPath =
            StructuralNodeIdentity.Create("parityWaves", "waveB");

        public static readonly string BuildPath =
            StructuralNodeIdentity.Create("parityPipeline", "build");

        public WorkflowEngine Engine { get; }
        public Guid RunId { get; }

        private readonly string _root;

        private WaveRun(string root, WorkflowEngine engine, Guid runId)
        {
            _root = root;
            Engine = engine;
            RunId = runId;
        }

        public static async Task<WaveRun> StartAsync(
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
                    $"Admission failed: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}"))}");
            var registration = await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    new FuwenZhinuProviderRuntimeIdentity(
                        admission.Receipt!.CatalogueSnapshotRevision,
                        admission.Receipt.ResolvedDescriptorSetFingerprint),
                    ports)
                .CreateAsync(workflowName, "1", admission, ct)
                .ConfigureAwait(false);
            var root = Path.Combine(
                Path.GetTempPath(), "guyabano-wave-parity", Guid.NewGuid().ToString("N"));
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
            return new WaveRun(root, engine, runId);
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

    private sealed class ScriptedWaveActivity : IActivityExecutor
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Calls(string activity)
        {
            lock (_calls)
            {
                return _calls.Where(pair => pair.Key == activity)
                    .SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value))
                    .ToArray();
            }
        }

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var name = request.Activity.Name;
            var argument = request.Arguments.Single().Value;
            lock (_calls)
            {
                _calls[name] = _calls.TryGetValue(name, out var count) ? count + 1 : 1;
            }

            string output = name switch
            {
                "parity.stepA" => AsText(argument) + ":a",
                "parity.stepB" => AsText(argument) + ":b",
                "parity.build" => "built:" + CountItems(argument),
                _ => throw new InvalidOperationException($"Unexpected activity '{name}'."),
            };
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return ValueTask.FromResult(
                ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }

        private static string AsText(RuntimeValue value) =>
            value is JsonRuntimeValue json
                ? json.Value.GetString()!
                : throw new InvalidOperationException($"Expected a text argument, got {value.GetType().Name}.");

        private static int CountItems(RuntimeValue value) =>
            value switch
            {
                ListRuntimeValue list => list.Items.Count,
                JsonRuntimeValue json when json.Value.ValueKind == JsonValueKind.Array
                    => json.Value.EnumerateArray().Count(),
                _ => throw new InvalidOperationException(
                    $"Expected a list argument, got {value.GetType().Name}."),
            };
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
