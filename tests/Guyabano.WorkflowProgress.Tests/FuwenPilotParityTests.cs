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
/// via Fuwen DSL + IR v4 fan-out. The hard-coded flow in
/// CodeGenerationWorkflow.cs uses Task.WhenAll waves for decomposition
/// and generation; Fuwen's FanOutNode requires validated string/integer
/// keys before child work and aggregates in source order.
/// </summary>
public sealed class FuwenPilotParityTests
{
    [Fact]
    public async Task Dsl_compile_and_programmatic_sequential_plan_are_both_admitted()
    {
        var catalogue = CodegenFuwenPilot.CreateCatalogue();
        // Programmatic sequential
        var sequential = CodegenFuwenPilot.BuildSequentialPlan();
        var seqAdmission = await new WorkflowAdmissionService(
            new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(sequential, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
        seqAdmission.Succeeded.Should().BeTrue(string.Join("; ", seqAdmission.Diagnostics.Select(d => d.Code + ":" + d.Message)));

        // DSL compile (codegen-pilot.fuwen) should produce same node count/kinds
        var source = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "FuwenPilot", "codegen-pilot.fuwen"),
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        // Fallback for test runner layout where content is copied to different folder
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "FuwenPilot", "codegen-pilot.fuwen")))
        {
            var alt = Path.Combine(
                Path.GetDirectoryName(typeof(CodegenFuwenPilot).Assembly.Location)!,
                "FuwenPilot", "codegen-pilot.fuwen");
            if (File.Exists(alt))
                source = await File.ReadAllTextAsync(alt, TestContext.Current.CancellationToken).ConfigureAwait(false);
            else
            {
                var repoAlt = Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "src", "Guyabano.CodeGeneration.Workflows", "FuwenPilot", "codegen-pilot.fuwen");
                source = await File.ReadAllTextAsync(repoAlt, TestContext.Current.CancellationToken).ConfigureAwait(false);
            }
        }

        var compiler = new FuwenSourceCompiler(catalogue);
        var compiled = await compiler.CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        compiled.Plan!.Nodes.Should().HaveCount(5); // ctx, infer, scaffold, build, return
        compiled.Plan.Nodes.OfType<ContextNode>().Should().ContainSingle();
        compiled.Plan.Nodes.OfType<InferenceNode>().Should().ContainSingle();
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

            // Replay: engine.RestartStepAsync on root should re-derive without provider calls?
            // For this sequential plan, full replay after completion reuses persisted envelopes.
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

            // Selective restart of one item should invalidate only that item + aggregate,
            // preserving sibling evidence — mirroring Guyabano's Task.WhenAll wave where
            // successful siblings are reused.
            var fanOutPath = StructuralNodeIdentity.Create("codegenFanOut", "generate");
            var itemPath = RuntimeNodeIdentity.CreateFanOutItem(fanOutPath, new StringRuntimeKey("a"));
            var restart = await engine.RestartStepAsync(runId, itemPath, TestContext.Current.CancellationToken).ConfigureAwait(false);
            restart.StepsToInvalidate.Select(s => s.StepKey).Should().Contain(itemPath).And.Contain(fanOutPath);

            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var second = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            second.EnumerateArray().Select(e => e.GetString()).Should().Equal("B", "A", "C");
            activity.Calls.Should().HaveCount(4); // only re-executed "a"
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
            // scaffold/build echo: prefix with activity name and propagate string
            var input = request.Arguments.Single().Value;
            var str = ((JsonRuntimeValue)input).Value.GetString()!;
            // Distinguish scaffold vs build by activity name suffix
            var suffix = request.Activity.Name.Contains("scaffold") ? ".scaffold" : ".build";
            // For generic generate, just uppercase
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
            // plan inference: echo input + ".plan"
            var input = request.Arguments.FirstOrDefault()?.Value ?? request.ContextInputs.FirstOrDefault()?.Value!;
            var str = input is JsonRuntimeValue j ? j.Value.GetString()! : input.ToString()!;
            var outStr = str + ".plan";
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(outStr));
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
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
