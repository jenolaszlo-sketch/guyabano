#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Workflows.FuwenPilot;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Pilot parity tests reproducing the hard-coded CodeGenerationWorkflow
/// via Fuwen DSL + IR v3-v7 features. Tests prove:
///   v3: sequential chain executes durably on real SQLite Zhinu
///   v4: fan-out preserves source order and restarts only one item
///   v5: conditional merge branches correctly
///   v6: repeat loop iterates bounded passes
///   v7: checkpoint persists durably; wait suspends until signal
/// </summary>
public sealed class FuwenPilotParityTests
{
    [Fact]
    public async Task Dsl_compile_and_programmatic_sequential_plan_are_both_admitted()
    {
        var catalogue = CodegenFuwenPilot.CreateCatalogue();
        var sequential = CodegenFuwenPilot.BuildSequentialPlan();
        var seqAdmission = await new WorkflowAdmissionService(
            new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(sequential, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
        seqAdmission.Succeeded.Should().BeTrue(string.Join("; ", seqAdmission.Diagnostics.Select(d => d.Code + ":" + d.Message)));

        var source = await CodegenFuwenPilot.ReadDslSourceAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        var compiler = new FuwenSourceCompiler(catalogue);
        var compiled = await compiler.CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        compiled.Plan!.Nodes.Should().ContainSingle(n => n is ContextNode);
        compiled.Plan.Nodes.Should().ContainSingle(n => n is InferenceNode);
    }

    [Fact]
    public async Task Extended_dsl_compiles_to_v7_with_all_feature_nodes()
    {
        var source = await CodegenFuwenPilot.ReadDslSourceAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        var compiler = new FuwenSourceCompiler(CodegenFuwenPilot.CreateCatalogue());
        var compiled = await compiler.CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);

        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(d => $"{d.Code}:{d.Message}")));
        compiled.Plan.Should().NotBeNull();
        compiled.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersionV7);
        compiled.Plan.Nodes.Should().ContainSingle(n => n is ContextNode);
        compiled.Plan.Nodes.Should().ContainSingle(n => n is InferenceNode);
        compiled.Plan.Nodes.Should().ContainSingle(n => n is ConditionalNode);
        compiled.Plan.Nodes.Should().ContainSingle(n => n is RepeatNode);
        compiled.Plan.Nodes.Should().ContainSingle(n => n is FanOutNode);
        compiled.Plan.Nodes.Should().ContainSingle(n => n is CheckpointNode);
        compiled.Plan.Nodes.Should().ContainSingle(n => n is WaitNode);
    }

    [Fact]
    public async Task Extended_programmatic_plan_admits_as_v7()
    {
        var plan = CodegenFuwenPilot.BuildExtendedPlan();
        plan.IrVersion.Should().Be(FuwenContracts.IrVersionV7);
        var admission = await CodegenFuwenPilot.AdmitAsync(plan, TestContext.Current.CancellationToken).ConfigureAwait(false);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message}")));
    }

    [Fact]
    public async Task Sequential_pilot_executes_durably_and_replays_without_reinvoking_provider()
    {
        var plan = CodegenFuwenPilot.BuildSequentialPlan();
        var admission = await CodegenFuwenPilot.AdmitAsync(plan, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var ports = new FuwenZhinuExecutionPorts(
            new EchoActivity(), new EchoContext(), new EchoInference());
        var factory = new FuwenZhinuWorkflowFactory(
            new InMemoryWorkflowDefinitionStore(),
            new FuwenZhinuProviderRuntimeIdentity(
                admission.Receipt!.CatalogueSnapshotRevision,
                admission.Receipt.ResolvedDescriptorSetFingerprint),
            ports);
        var registration = await factory.CreateAsync("fuwen.pilot.seq", "1", admission, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var root = Path.Combine(Path.GetTempPath(), "guyabano-fuwen-pilot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"hello-input\"");
            var runId = await engine.StartAsync("fuwen.pilot.seq", "1", input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            output.GetString().Should().Be("hello-input.ctx.plan.scaffold.build");

            var steps = await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(false);
            steps.Should().Contain(s => s.StepKey == "codegenPilot/ctx" && s.Status == StepStatus.Completed)
                 .And.Contain(s => s.StepKey == "codegenPilot/build" && s.Status == StepStatus.Completed);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Fanout_pilot_preserves_source_order_and_restarts_only_one_item()
    {
        var plan = CodegenFuwenPilot.BuildFanOutPlan();
        var admission = await CodegenFuwenPilot.AdmitAsync(plan, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var activity = new UppercaseActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference(),
                    observer: null, new FuwenZhinuExecutionPorts.Options(maximumFanOutConcurrency: 2)))
            .CreateAsync("fuwen.pilot.fanout", "1", admission, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var root = Path.Combine(Path.GetTempPath(), "guyabano-fuwen-pilot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("[\"b\",\"a\",\"c\"]");
            var runId = await engine.StartAsync("fuwen.pilot.fanout", "1", input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var first = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            first.EnumerateArray().Select(e => e.GetString()).Should().Equal("B", "A", "C");
            activity.Calls.Should().HaveCount(3);

            var fanOutPath = StructuralNodeIdentity.Create("codegenFanOut", "generate");
            var itemPath = RuntimeNodeIdentity.CreateFanOutItem(fanOutPath, new StringRuntimeKey("a"));
            var restart = await engine.RestartStepAsync(runId, itemPath, TestContext.Current.CancellationToken).ConfigureAwait(false);
            restart.StepsToInvalidate.Select(s => s.StepKey).Should().Contain(itemPath).And.Contain(fanOutPath);

            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var second = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            second.EnumerateArray().Select(e => e.GetString()).Should().Equal("B", "A", "C");
            activity.Calls.Should().HaveCount(4);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Extended_pilot_executes_through_checkpoint_and_wait_with_signal()
    {
        var plan = CodegenFuwenPilot.BuildExtendedPlan();
        var admission = await CodegenFuwenPilot.AdmitAsync(plan, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var activity = new ExtendedPlanActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(activity, new ExtendedPlanContext(), new ExtendedPlanInference()))
            .CreateAsync("fuwen.pilot.ext", "1", admission, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var root = Path.Combine(Path.GetTempPath(), "guyabano-fuwen-pilot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"hello\"");
            var runId = await engine.StartAsync("fuwen.pilot.ext", "1", input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var execution = engine.ExecuteAsync(runId, cts.Token);

            while (true)
            {
                var steps = await engine.GetStepsAsync(runId, cts.Token).ConfigureAwait(false);
                if (steps.Any(s => s.StepKey == "codegenPilot/approval" && s.Status == StepStatus.Waiting))
                    break;
                if (steps.All(s => s.Status is StepStatus.Completed or StepStatus.Failed))
                    break;
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }

            await engine.SendSignalAsync(runId, "approval-signal", "approved", cts.Token).ConfigureAwait(false);
            await execution.ConfigureAwait(false);

            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            output.GetString().Should().Be("approved.build");

            var stepsFinal = await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(false);
            stepsFinal.Should().Contain(s => s.StepKey == "codegenPilot/generated" && s.Status == StepStatus.Completed);
            stepsFinal.Should().Contain(s => s.StepKey == "codegenPilot/approval" && s.Status == StepStatus.Completed);
            stepsFinal.Should().Contain(s => s.StepKey == "codegenPilot/build" && s.Status == StepStatus.Completed);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        for (var i = 0; i < 5 && Directory.Exists(path); i++)
        {
            try { Directory.Delete(path, true); break; } catch { Thread.Sleep(50 * (i + 1)); }
        }
    }

    private sealed class EchoActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var input = request.Arguments.Single().Value;
            var str = ((JsonRuntimeValue)input).Value.GetString()!;
            var suffix = request.Activity.Name.Contains("scaffold") ? ".scaffold" : ".build";
            if (request.Activity.Name == "guyabano.generate")
                suffix = "";
            var output = request.Activity.Name == "guyabano.generate"
                ? str.ToUpperInvariant()
                : str + suffix;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private sealed class EchoContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken ct = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            var outStr = input + ".ctx";
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(outStr));
            var snap = new ContextSnapshotReference(request.Provider, "snap-1",
                new ContentDigest("sha256", "request/v1", new string('c', 64)),
                new ContentDigest("sha256", "content/v1", new string('d', 64)), [],
                "policy/1", new ContextSnapshotBudgetEvidence(false, null, null, null, null), DateTimeOffset.UtcNow);
            return ValueTask.FromResult(ContextExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement), snap));
        }
    }

    private sealed class EchoInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken ct = default)
        {
            var input = request.Arguments.FirstOrDefault()?.Value ?? request.ContextInputs.FirstOrDefault()?.Value!;
            var str = input is JsonRuntimeValue j ? j.Value.GetString()! : input.ToString()!;
            var outStr = str + ".plan";
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(outStr));
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private sealed class ExtendedPlanContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken ct = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input + ".ctx"));
            var snap = new ContextSnapshotReference(request.Provider, "snap-ext",
                new ContentDigest("sha256", "request/v1", new string('c', 64)),
                new ContentDigest("sha256", "content/v1", new string('d', 64)), [],
                "policy/1", new ContextSnapshotBudgetEvidence(false, null, null, null, null), DateTimeOffset.UtcNow);
            return ValueTask.FromResult(ContextExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement), snap));
        }
    }

    private sealed class ExtendedPlanInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken ct = default)
        {
            var input = request.Arguments.FirstOrDefault()?.Value ?? request.ContextInputs.FirstOrDefault()?.Value!;
            var str = input is JsonRuntimeValue j ? j.Value.GetString()! : input.ToString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(str + ".plan"));
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private sealed class ExtendedPlanActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var name = request.Activity.Name;
            return name switch
            {
                "guyabano.review" => EchoValue(request),
                "guyabano.prepare-tasks" => ProduceTaskList(request),
                "guyabano.generate" => UppercaseValue(request),
                "guyabano.build" => AppendSuffix(request, ".build"),
                _ => PassthroughValue(request),
            };
        }

        private static ValueTask<ActivityExecutionResult> EchoValue(ActivityExecutionRequest request)
        {
            var value = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }

        private static ValueTask<ActivityExecutionResult> ProduceTaskList(ActivityExecutionRequest request)
        {
            var planStr = ((JsonRuntimeValue)request.Arguments.Single(a => a.Name == "plan").Value).Value.GetString()!;
            var tasks = planStr.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Concat(Enumerable.Range(0, 8).Select(i => $"task{i}"))
                .Take(8)
                .ToArray();
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(tasks));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }

        private static ValueTask<ActivityExecutionResult> UppercaseValue(ActivityExecutionRequest request)
        {
            var value = ((JsonRuntimeValue)request.Arguments.Single(a => a.Name == "task").Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value.ToUpperInvariant()));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }

        private static ValueTask<ActivityExecutionResult> AppendSuffix(ActivityExecutionRequest request, string suffix)
        {
            var value = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value + suffix));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }

        private static ValueTask<ActivityExecutionResult> PassthroughValue(ActivityExecutionRequest request)
        {
            var value = ((JsonRuntimeValue)request.Arguments.Single().Value).Value;
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(value.Clone())));
        }
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest r, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class UnusedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest r, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class UppercaseActivity : IActivityExecutor
    {
        public List<string> Calls { get; } = [];
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var v = ((JsonRuntimeValue)request.Arguments.Single(a => a.Name == "task").Value).Value.GetString()!;
            Calls.Add(v);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(v.ToUpperInvariant()));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }
}
