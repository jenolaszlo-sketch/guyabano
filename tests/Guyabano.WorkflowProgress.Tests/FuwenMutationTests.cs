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
/// Mutation at the Fuwen level (Guyabano milestone test 1 core mechanic):
/// an admitted v1 plan (chain a-b) migrates to an admitted v2 plan (a-b-c)
/// via cross-version fork. Completed steps are preserved; only the new
/// node executes; lineage is recorded.
/// </summary>
public sealed class FuwenMutationTests
{
    [Fact]
    public async Task Admitted_plan_migrates_across_versions_preserving_completed_steps()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogue = CodegenFuwenPilot.CreateCatalogue();
        var activity = new CountingActivity();

        var v1 = BuildChainPlan("1", withC: false);
        var admission1 = await AdmitAsync(v1, catalogue, ct);
        var v2 = BuildChainPlan("2", withC: true);
        var admission2 = await AdmitAsync(v2, catalogue, ct);

        var root = Path.Combine(Path.GetTempPath(), "guyabano-fuwen-mutation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            var registry = new WorkflowRegistry();
            var identity = new FuwenZhinuProviderRuntimeIdentity(
                admission1.Receipt!.CatalogueSnapshotRevision,
                admission1.Receipt.ResolvedDescriptorSetFingerprint);
            // Both admissions share the catalogue snapshot; the v2 receipt
            // carries the same snapshot revision and descriptor fingerprint.
            // The v2 ports additionally trust v1's execution fingerprint so
            // fork-copied step evidence admits under the new plan.
            var factory1 = new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(), identity,
                new FuwenZhinuExecutionPorts(
                    activity, new UnusedContext(), new UnusedInference()));
            var registration1 = await factory1.CreateAsync(
                "mutate", "1", admission1, ct);
            registration1.Register(registry);
            var ports2 = new FuwenZhinuExecutionPorts(
                activity, new UnusedContext(), new UnusedInference())
            {
                PriorExecutionFingerprints = new HashSet<string>(
                    [admission1.Receipt.ExecutionFingerprint], StringComparer.Ordinal),
            };
            var factory2 = new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(), identity, ports2);
            var registration2 = await factory2.CreateAsync(
                "mutate", "2", admission2, ct);
            registration2.Register(registry);
            await using var engine = new WorkflowEngine(store, registry,
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"goal\"");
            var sourceId = await engine.StartAsync("mutate", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(sourceId, ct);
            var sourceOutput = await engine.WaitForCompletionAsync<JsonElement>(sourceId, cancellationToken: ct);
            sourceOutput.GetString().Should().Be("goal.a.b");
            activity.Calls.Should().Equal("a:goal", "b:goal.a");

            // Fork at the new node: a and b are reused (their v1 evidence is
            // declared via PriorExecutionFingerprints), c runs, and the return
            // node is superseded and re-runs against the new sequence a-b-c.
            var migratedId = await engine.ForkAsync(
                sourceId,
                "mutate/c",
                new ForkRunOptions
                {
                    TargetWorkflowVersion = "2",
                    Actor = "test",
                    Reason = "planning complete; start implementation",
                },
                ct);

            var migrated = await engine.GetRunAsync(migratedId, ct);
            migrated!.WorkflowVersion.Should().Be("2");
            migrated.SourceRunId.Should().Be(sourceId);

            await engine.ExecuteAsync(migratedId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(migratedId, cancellationToken: ct);
            output.GetString().Should().Be("goal.a.b.c");
            activity.Calls.Should().Equal("a:goal", "b:goal.a", "c:goal.a.b");

            var completed = await engine.GetRunAsync(migratedId, ct);
            completed!.Status.Should().Be(WorkflowStatus.Completed);
            var source = await engine.GetRunAsync(sourceId, ct);
            source!.Status.Should().Be(WorkflowStatus.Completed);
        }
        finally
        {
            for (var i = 0; i < 5 && Directory.Exists(root); i++)
            {
                try { Directory.Delete(root, true); break; } catch { Thread.Sleep(50 * (i + 1)); }
            }
        }
    }

    private static WorkflowPlan BuildChainPlan(string version, bool withC)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var aPath = StructuralNodeIdentity.Create("mutate", "a");
        var bPath = StructuralNodeIdentity.Create("mutate", "b");
        var cPath = StructuralNodeIdentity.Create("mutate", "c");
        var returnPath = StructuralNodeIdentity.Create("mutate", "return_result");
        var builder = new WorkflowPlanBuilder("mutate", version, str, str, "routing/1")
            .AddNode(new ActivityNode("a", aPath, CodegenFuwenPilot.ScaffoldDescriptor,
                [new ArgumentBinding("plan", new InputBinding([]))], str))
            .AddNode(new ActivityNode("b", bPath, CodegenFuwenPilot.ScaffoldDescriptor,
                [new ArgumentBinding("plan", new NodeOutputBinding(aPath, []))], str));
        var phases = new List<WorkflowExecutionPhase>([
            new WorkflowExecutionPhase([aPath]),
            new WorkflowExecutionPhase([bPath]),
        ]);
        WorkflowExecutionRegion bodyRegion;
        if (withC)
        {
            builder.AddNode(new ActivityNode("c", cPath, CodegenFuwenPilot.ScaffoldDescriptor,
                [new ArgumentBinding("plan", new NodeOutputBinding(bPath, []))], str));
            phases.Add(new WorkflowExecutionPhase([cPath]));
            phases.Add(new WorkflowExecutionPhase([returnPath]));
            bodyRegion = new WorkflowExecutionRegion("mutate", phases);
            builder
                .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(cPath, [])))
                .SetExecutionOrder(new WorkflowExecutionOrder([bodyRegion]));
            return builder.BuildV3();
        }
        phases.Add(new WorkflowExecutionPhase([returnPath]));
        bodyRegion = new WorkflowExecutionRegion("mutate", phases);
        builder
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(bPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([bodyRegion]));
        return builder.BuildV3();
    }

    private static async Task<WorkflowAdmissionResult> AdmitAsync(
        WorkflowPlan plan, ITrustedCatalogue catalogue, CancellationToken ct)
    {
        var admission = await new WorkflowAdmissionService(
                new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct).ConfigureAwait(false);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
        return admission;
    }

    private sealed class CountingActivity : IActivityExecutor
    {
        public List<string> Calls { get; } = [];
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var node = request.Invocation.StructuralPath.Split('/')[^1];
            var input = ((JsonRuntimeValue)request.Arguments.Single(a => a.Name == "plan").Value).Value.GetString()!;
            Calls.Add($"{node}:{input}");
            var output = input + "." + node;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
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
}
