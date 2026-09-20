#pragma warning disable xUnit1030
using System.Text.Json;
using Penghou.Guihua.Baize;
using Penghou.Guihua;
using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Planning;
using Guyabano.Llm.Prompting;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Spec §20 Test 4 (architecture change): revising the architecture to add a
/// Cache component cascades regeneration through contracts, components,
/// execution graph, bindings, and Fuwen, then mutates the workflow. Re-derived
/// artifacts whose content is unchanged are retained at their revisions;
/// only the new branch and its changed join execute.
/// </summary>
public sealed class ArchitectureChangeMutationTests : IDisposable
{
    private const string WorkflowId = "plan";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-architecture-change",
        Guid.NewGuid().ToString("N"));

    private static string Digest(char c) => new string(c, 64);

    private static TrustedCatalogueDescriptor ActivityDescriptor(
        string name, char digest, params string[] parameters)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new TrustedCatalogueDescriptor(
            new DescriptorReference(DescriptorKind.Activity, name, "1",
                new ContentDigest("sha256", "descriptor/v1", Digest(digest))),
            callableContract: new CallableContract(
                new CallableSignature([.. parameters.Select(p => new CallableParameter(p, str))], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe));
    }

    private static TrustedCatalogueDescriptor[] Descriptors() =>
    [
        ActivityDescriptor("sample.stage", 'a', "value"),
        ActivityDescriptor("sample.task", 'b', "value"),
        ActivityDescriptor("sample.join", 'c', "first", "second"),
        ActivityDescriptor("sample.join3", 'e', "first", "second", "third"),
        ActivityDescriptor("sample.check", 'd', "value"),
    ];

    private static ITrustedCatalogue Catalogue() =>
        new InMemoryTrustedCatalogue(Descriptors());

    private static string V1Dsl() => $$"""
        workflow plan(input: string) -> string {
          activity domain = activity "sample.stage@1#{{Digest('a')}}" (value: input;) -> string;
          activity topology = activity "sample.stage@1#{{Digest('a')}}" (value: domain;) -> string;
          activity contracts = activity "sample.stage@1#{{Digest('a')}}" (value: topology;) -> string;
          activity components = activity "sample.stage@1#{{Digest('a')}}" (value: contracts;) -> string;
          return components;
        }
        """;

    private static string V2Dsl() => $$"""
        workflow plan(input: string) -> string {
          activity domain = activity "sample.stage@1#{{Digest('a')}}" (value: input;) -> string;
          activity topology = activity "sample.stage@1#{{Digest('a')}}" (value: domain;) -> string;
          activity contracts = activity "sample.stage@1#{{Digest('a')}}" (value: topology;) -> string;
          activity components = activity "sample.stage@1#{{Digest('a')}}" (value: contracts;) -> string;
          activity implement_todos = activity "sample.task@1#{{Digest('b')}}" (value: components;) -> string;
          activity implement_reporting = activity "sample.task@1#{{Digest('b')}}" (value: components;) -> string;
          activity integrate = activity "sample.join@1#{{Digest('c')}}" (first: implement_todos, second: implement_reporting;) -> string;
          activity test = activity "sample.check@1#{{Digest('d')}}" (value: integrate;) -> string;
          return test;
        }
        """;

    private static string V3Dsl() => $$"""
        workflow plan(input: string) -> string {
          activity domain = activity "sample.stage@1#{{Digest('a')}}" (value: input;) -> string;
          activity topology = activity "sample.stage@1#{{Digest('a')}}" (value: domain;) -> string;
          activity contracts = activity "sample.stage@1#{{Digest('a')}}" (value: topology;) -> string;
          activity components = activity "sample.stage@1#{{Digest('a')}}" (value: contracts;) -> string;
          activity implement_todos = activity "sample.task@1#{{Digest('b')}}" (value: components;) -> string;
          activity implement_reporting = activity "sample.task@1#{{Digest('b')}}" (value: components;) -> string;
          activity implement_cache = activity "sample.task@1#{{Digest('b')}}" (value: components;) -> string;
          activity integrate = activity "sample.join3@1#{{Digest('e')}}" (first: implement_cache, second: implement_reporting, third: implement_todos;) -> string;
          activity test = activity "sample.check@1#{{Digest('d')}}" (value: integrate;) -> string;
          return test;
        }
        """;

    [Fact]
    public async Task Architecture_change_regenerates_the_cascade_and_retains_unaffected_branches()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogue = Catalogue();
        var compiler = new WorkflowCompiler(
            catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", []));
        var staged = CreateStagedPayloads(withCache: false);
        var activity = new TaskActivity(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["domain"] = JsonSerializer.Serialize(staged.Domain),
            ["topology"] = JsonSerializer.Serialize(staged.Topology),
            ["contracts"] = JsonSerializer.Serialize(staged.Contracts),
            ["components"] = JsonSerializer.Serialize(staged.Components),
        });

        Directory.CreateDirectory(_root);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(_root, "workflow.db"),
            Pooling = false,
        });
        var registry = new WorkflowRegistry();
        await using var engine = new WorkflowEngine(store, registry,
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

        // v1: planning, then publish the baseline cascade and v2 design.
        var admission1 = await AdmitDslAsync(V1Dsl(), catalogue, compiler, ct);
        var registration1 = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission1),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync(WorkflowId, "1", admission1, ct);
        registration1.Register(registry);
        using var input = JsonDocument.Parse("\"goal\"");
        var v1Id = await engine.StartAsync(
            WorkflowId, "1", input.RootElement.Clone(), cancellationToken: ct);
        await engine.ExecuteAsync(v1Id, ct);
        await engine.WaitForCompletionAsync<JsonElement>(v1Id, cancellationToken: ct);

        var artifactCatalog = new PlanningArtifactCatalog(
            new FileSystemArtifactRepository(Path.Combine(_root, "artifacts")));
        var publisher = new StagedPlanningArtifactPublisher(artifactCatalog);
        var artifacts = new StagedPlanningArtifacts(
            JsonSerializer.Deserialize<DomainDiscovery>(activity.Outputs["domain"])!,
            JsonSerializer.Deserialize<SolutionTopology>(activity.Outputs["topology"])!,
            JsonSerializer.Deserialize<List<BoundedContextContractCatalog>>(activity.Outputs["contracts"])!,
            JsonSerializer.Deserialize<List<BoundedContextComponentManifest>>(activity.Outputs["components"])!);
        var versions = await publisher.PublishAsync(WorkflowId, artifacts, cancellationToken: ct);
        var design = StagedExecutionGraphBuilder.Build(
            artifacts, versions, TrustedByRole("sample.join", 'c'));
        await publisher.PublishExecutionDesignAsync(
            WorkflowId, design,
            [.. versions.Contracts.Values, .. versions.Components.Values],
            cancellationToken: ct);

        // v2: implement, join, and test.
        var admission2 = await AuthorAsync(design, V2Dsl(), ct);
        var registration2 = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission2),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference())
                {
                    PriorExecutionFingerprints = new HashSet<string>(
                        [admission1.Receipt!.ExecutionFingerprint], StringComparer.Ordinal),
                })
            .CreateAsync(WorkflowId, "2", admission2, ct);
        registration2.Register(registry);
        var v2Id = await engine.ForkAsync(
            v1Id, "plan/implement_todos",
            new ForkRunOptions { TargetWorkflowVersion = "2", Actor = "test", Reason = "start implementation" },
            ct);
        await engine.ExecuteAsync(v2Id, ct);
        await engine.WaitForCompletionAsync<JsonElement>(v2Id, cancellationToken: ct);

        // Architecture change: the system gains a Cache component. Publish the
        // revised topology, then cascade invalidation through every layer.
        var extended = CreateStagedPayloads(withCache: true);
        var topologyV2 = await artifactCatalog.PublishAsync(
            new PublishPlanningArtifactRequest<SolutionTopology>(
                WorkflowId,
                new PlanningArtifactKey(StagedPlanningArtifactPublisher.TopologyKind, "main"),
                1,
                StagedPlanningArtifactPublisher.PlanTopology,
                extended.Topology,
                Inputs: [versions.Domain],
                State: PlanningArtifactState.Valid),
            ct);
        var impact = await artifactCatalog.InvalidateAsync(WorkflowId, topologyV2.Version, ct);
        impact.Affected.Select(record => record.Version.Value).Should().BeEquivalentTo(
            "solution-topology/main@1",
            "contracts/todos@1",
            "contracts/reporting@1",
            "components/todos@1",
            "components/reporting@1",
            "execution-graph/main@1",
            "bindings/main@1");

        // Regenerate: re-derivation confirms the Todos/Reporting contracts and
        // components are unchanged, so they are retained; only genuinely new
        // artifacts get fresh revisions.
        foreach (var version in versions.Contracts.Values.Concat(versions.Components.Values))
        {
            var retained = await artifactCatalog.RevalidateAsync(WorkflowId, version, ct);
            retained.State.Should().Be(PlanningArtifactState.Valid);
        }
        var cacheContract = await artifactCatalog.PublishAsync(
            new PublishPlanningArtifactRequest<BoundedContextContractCatalog>(
                WorkflowId,
                StagedPlanningArtifactPublisher.ContextKey(
                    StagedPlanningArtifactPublisher.ContractKind, "Cache"),
                1,
                StagedPlanningArtifactPublisher.PlanContracts,
                extended.Contracts.Single(c => c.BoundedContextName == "Cache"),
                Inputs: [topologyV2.Version],
                State: PlanningArtifactState.Valid),
            ct);
        var cacheComponent = await artifactCatalog.PublishAsync(
            new PublishPlanningArtifactRequest<BoundedContextComponentManifest>(
                WorkflowId,
                StagedPlanningArtifactPublisher.ContextKey(
                    StagedPlanningArtifactPublisher.ComponentKind, "Cache"),
                1,
                StagedPlanningArtifactPublisher.PlanComponents,
                extended.Components.Single(c => c.BoundedContextName == "Cache"),
                Inputs: [cacheContract.Version],
                State: PlanningArtifactState.Valid),
            ct);

        // The cascade now reads: revised topology, retained contracts and
        // components, new cache artifacts, and a regenerated design.
        var current = await artifactCatalog.ListCurrentAsync(WorkflowId, ct);
        current.Single(record => record.Key == new PlanningArtifactKey("contracts", "todos"))
            .Revision.Should().Be(1);
        current.Single(record => record.Key == new PlanningArtifactKey("contracts", "cache"))
            .Revision.Should().Be(1);

        var extendedArtifacts = new StagedPlanningArtifacts(
            artifacts.Domain, extended.Topology, extended.Contracts, extended.Components);
        var extendedVersions = new StagedPlanningArtifactVersions(
            versions.Domain,
            topologyV2.Version,
            new Dictionary<string, PlanningArtifactVersion>(StringComparer.Ordinal)
            {
                ["Todos"] = versions.Contracts["Todos"],
                ["Reporting"] = versions.Contracts["Reporting"],
                ["Cache"] = cacheContract.Version,
            },
            new Dictionary<string, PlanningArtifactVersion>(StringComparer.Ordinal)
            {
                ["Todos"] = versions.Components["Todos"],
                ["Reporting"] = versions.Components["Reporting"],
                ["Cache"] = cacheComponent.Version,
            });
        var extendedDesign = StagedExecutionGraphBuilder.Build(
            extendedArtifacts,
            extendedVersions,
            new Dictionary<string, TrustedCatalogueDescriptor>(StringComparer.Ordinal)
            {
                [PlannedExecutionRoles.Implement] = ActivityDescriptor("sample.task", 'b', "value"),
                [PlannedExecutionRoles.Integrate] = ActivityDescriptor("sample.join3", 'e', "first", "second", "third"),
                [PlannedExecutionRoles.Verify] = ActivityDescriptor("sample.check", 'd', "value"),
            });
        var designVersions = await publisher.PublishExecutionDesignAsync(
            WorkflowId, extendedDesign,
            [.. extendedVersions.Contracts.Values, .. extendedVersions.Components.Values],
            cancellationToken: ct);
        designVersions.Graph.Value.Should().Be("execution-graph/main@2");

        // v3: regenerate Fuwen with the cache branch and mutate.
        var admission3 = await AuthorAsync(extendedDesign, V3Dsl(), ct);
        var registration3 = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission3),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference())
                {
                    PriorExecutionFingerprints = new HashSet<string>(
                        [
                            admission1.Receipt!.ExecutionFingerprint,
                            admission2.Receipt!.ExecutionFingerprint,
                        ],
                        StringComparer.Ordinal),
                })
            .CreateAsync(WorkflowId, "3", admission3, ct);
        registration3.Register(registry);
        var v3Id = await engine.ForkAsync(
            v2Id, "plan/implement_cache",
            new ForkRunOptions { TargetWorkflowVersion = "3", Actor = "test", Reason = "architecture gained cache" },
            ct);
        var v3 = await engine.GetRunAsync(v3Id, ct);
        v3!.WorkflowVersion.Should().Be("3");
        v3.SourceRunId.Should().Be(v2Id);
        await engine.ExecuteAsync(v3Id, ct);
        await engine.WaitForCompletionAsync<JsonElement>(v3Id, cancellationToken: ct);

        // Unaffected branches retained; the new branch and changed join ran.
        CallsFor(activity, "implement_todos").Should().HaveCount(1);
        CallsFor(activity, "implement_reporting").Should().HaveCount(1);
        CallsFor(activity, "implement_cache").Should().HaveCount(1);
        CallsFor(activity, "integrate").Should().HaveCount(2);
        CallsFor(activity, "test").Should().HaveCount(2);
        (await engine.GetRunAsync(v3Id, ct))!.Status.Should().Be(WorkflowStatus.Completed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            for (var i = 0; i < 5; i++)
            {
                try { Directory.Delete(_root, true); break; }
                catch { Thread.Sleep(50 * (i + 1)); }
            }
        }
    }

    private static Dictionary<string, TrustedCatalogueDescriptor> TrustedByRole(
        string joinName, char joinDigest) =>
        new(StringComparer.Ordinal)
        {
            [PlannedExecutionRoles.Implement] = ActivityDescriptor("sample.task", 'b', "value"),
            [PlannedExecutionRoles.Integrate] = ActivityDescriptor(joinName, joinDigest, "first", "second"),
            [PlannedExecutionRoles.Verify] = ActivityDescriptor("sample.check", 'd', "value"),
        };

    private static FuwenZhinuProviderRuntimeIdentity IdentityFor(WorkflowAdmissionResult admission) =>
        new(
            admission.Receipt!.CatalogueSnapshotRevision,
            admission.Receipt.ResolvedDescriptorSetFingerprint);

    private static async Task<WorkflowAdmissionResult> AdmitDslAsync(
        string dsl, ITrustedCatalogue catalogue, WorkflowCompiler compiler, CancellationToken ct)
    {
        var compiled = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(dsl, cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(
            string.Join("; ", compiled.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
        var admission = await new WorkflowAdmissionService(compiler)
            .AdmitAsync(compiled.Plan!, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
        return admission;
    }

    private async Task<WorkflowAdmissionResult> AuthorAsync(
        PlanningDesign design, string dsl, CancellationToken ct)
    {
        var router = new CannedPlanRouter(dsl);
        var author = new WorkflowAuthor(
            router,
            new WorkflowAuthoringPromptBuilder(
                new ScribanPromptTemplateEngine(new EmbeddedPromptLoader())),
            Catalogue(),
            maxAttempts: 3);
        var result = await author.AuthorFromPlanAsync(
            "Implement ticket classification.",
            PlanningGraphSummary.Render(design),
            CatalogueSummaryBuilder.Render(Descriptors()),
            "stub-author",
            4000,
            ct);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Admission.Should().NotBeNull();
        return result.Admission!;
    }

    private static (DomainDiscovery Domain, SolutionTopology Topology,
        List<BoundedContextContractCatalog> Contracts,
        List<BoundedContextComponentManifest> Components) CreateStagedPayloads(bool withCache)
    {
        var contexts = new List<BoundedContextPlan>
        {
            new()
            {
                Name = "Todos",
                Purpose = "Manage todos.",
                CapabilityNames = [],
                DependsOnContextNames = [],
                InboundAdapters = [],
                OutboundAdapters = [],
            },
            new()
            {
                Name = "Reporting",
                Purpose = "Report on todos.",
                CapabilityNames = [],
                DependsOnContextNames = [],
                InboundAdapters = [],
                OutboundAdapters = [],
            },
        };
        if (withCache)
        {
            contexts.Add(new BoundedContextPlan
            {
                Name = "Cache",
                Purpose = "Cache classifications.",
                CapabilityNames = [],
                DependsOnContextNames = [],
                InboundAdapters = [],
                OutboundAdapters = [],
            });
        }

        List<BoundedContextContractCatalog> contracts =
        [
            new() { BoundedContextName = "Todos", Contracts = [], Decisions = [], InferredDefaults = [] },
            new() { BoundedContextName = "Reporting", Contracts = [], Decisions = [], InferredDefaults = [] },
        ];
        List<BoundedContextComponentManifest> components =
        [
            new() { BoundedContextName = "Todos", Components = [], Decisions = [], InferredDefaults = [] },
            new() { BoundedContextName = "Reporting", Components = [], Decisions = [], InferredDefaults = [] },
        ];
        if (withCache)
        {
            contracts.Add(new BoundedContextContractCatalog
            { BoundedContextName = "Cache", Contracts = [], Decisions = [], InferredDefaults = [] });
            components.Add(new BoundedContextComponentManifest
            { BoundedContextName = "Cache", Components = [], Decisions = [], InferredDefaults = [] });
        }

        return (
            new DomainDiscovery
            {
                Mission = new ProductMission
                {
                    GuidingIntent = "Classify tickets.",
                    SuccessOutcomes = [],
                    Constraints = [],
                    NonGoals = [],
                },
                Title = "Ticket Classification",
                Summary = "Classifies tickets.",
                Terms = [],
                Capabilities = [],
                UseCases = [],
                QualityAttributes = [],
                Assumptions = [],
                InferredDefaults = [],
                ProductAmbiguities = [],
            },
            new SolutionTopology
            {
                Solution = new PlannedSolution { Name = "Support", Path = "Support.sln" },
                Projects = [],
                BoundedContexts = contexts,
                Modules = [],
                Decisions = [],
            },
            contracts,
            components);
    }

    private static IReadOnlyList<string> CallsFor(TaskActivity activity, string node) =>
        activity.Calls.Where(call => call.StartsWith(node + ":", StringComparison.Ordinal)).ToArray();

    private sealed class TaskActivity(Dictionary<string, string> canned) : IActivityExecutor
    {
        public List<string> Calls { get; } = [];
        public Dictionary<string, string> Outputs { get; } = new(StringComparer.Ordinal);

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var node = request.Invocation.StructuralPath.Split('/')[^1];
            var values = request.Arguments
                .Select(argument => ((JsonRuntimeValue)argument.Value).Value.GetString()!)
                .ToArray();
            var input = values.Length == 1 ? values[0] : string.Join("+", values);
            Calls.Add($"{node}:{input}");
            var output = canned.TryGetValue(node, out var staged)
                ? staged
                : input + "." + node;
            Outputs[node] = output;
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
                RuntimeValue.FromJson(document.RootElement.Clone())));
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

    private sealed class CannedPlanRouter(string dsl) : ILlmRouter
    {
        public List<LlmRequest> Requests { get; } = [];

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return StreamSingle(dsl);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ModelStrategy strategy, LlmRequest request, CancellationToken cancellationToken) =>
            StreamAsync(strategy.ToString(), request, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(model, builder.Build(ModelStrategy.Auto), cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ModelStrategy strategy, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(strategy.ToString(), builder, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
            string route, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(route, builder, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
            string route, LlmRequest request, CancellationToken cancellationToken) =>
            StreamAsync(route, request, cancellationToken);

        public ResolvedEndpoint Resolve(string model) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(string model, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ResolvedEndpoint Resolve(ModelStrategy strategy) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(ModelStrategy strategy, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveRouteAsync(string route, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainModelAsync(string model, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainStrategyAsync(ModelStrategy strategy, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainRouteAsync(string route, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        private static async IAsyncEnumerable<LlmStreamEvent> StreamSingle(
            string delta,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new LlmStreamEvent(delta, null, "stop", null, null, null, null, null, null);
        }
    }
}
