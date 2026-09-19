#pragma warning disable xUnit1030
using System.Text.Json;
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
/// Spec §20 Test 3 (contract change): revising the Todos contract marks only
/// its downstream planning region stale while the independent Reporting branch
/// stays valid; the affected Fuwen nodes are regenerated; a second mutation
/// reruns exactly the affected execution subgraph and preserves the rest.
/// </summary>
public sealed class ContractChangeMutationTests : IDisposable
{
    private const string WorkflowId = "plan";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-contract-change",
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
        ActivityDescriptor("sample.check", 'd', "value"),
        ActivityDescriptor("sample.task2", 'e', "value"),
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
          activity implement_todos = activity "sample.task2@1#{{Digest('e')}}" (value: components;) -> string;
          activity implement_reporting = activity "sample.task@1#{{Digest('b')}}" (value: components;) -> string;
          activity integrate = activity "sample.join@1#{{Digest('c')}}" (first: implement_todos, second: implement_reporting;) -> string;
          activity test = activity "sample.check@1#{{Digest('d')}}" (value: integrate;) -> string;
          return test;
        }
        """;

    [Fact]
    public async Task Contract_change_reruns_only_the_affected_execution_subgraph()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogue = Catalogue();
        var compiler = new WorkflowCompiler(
            catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", []));
        var staged = CreateStagedPayloads();
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

        // v1: planning-only workflow.
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

        // Publish the planning cascade and derive the v2 execution design.
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
            artifacts, versions, TrustedByRole("sample.task", 'b', 'c', 'd'));
        await publisher.PublishExecutionDesignAsync(
            WorkflowId, design,
            [.. versions.Contracts.Values, .. versions.Components.Values],
            cancellationToken: ct);

        // v2: author from the design, fork, and execute the implementation.
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
        CallsFor(activity, "implement_todos").Should().HaveCount(1);
        CallsFor(activity, "implement_reporting").Should().HaveCount(1);

        // Revise only the Todos contract. The change is material: the
        // classifier gains a required options operation.
        var todosKey = new PlanningArtifactKey("contracts", "todos");
        var revisedTodos = new BoundedContextContractCatalog
        {
            BoundedContextName = "Todos",
            Contracts =
            [
                new StagedContract
                {
                    Name = "ITodoClassifier",
                    Kind = "Interface",
                    ModuleName = "Todos",
                    Purpose = "Classifies todos with options.",
                    Members = ["ClassificationResult Classify(Todo todo, ClassifyOptions options)"],
                    CapabilityNames = ["ClassifyTodo"],
                },
            ],
            Decisions = [],
            InferredDefaults = [],
        };
        var todosV2 = await artifactCatalog.PublishAsync(
            new PublishPlanningArtifactRequest<BoundedContextContractCatalog>(
                WorkflowId, todosKey, 1,
                StagedPlanningArtifactPublisher.PlanContracts,
                revisedTodos,
                Inputs: [versions.Topology],
                State: PlanningArtifactState.Valid),
            ct);

        // Impact: the Todos downstream region goes stale; Reporting is untouched.
        var impact = await artifactCatalog.InvalidateAsync(WorkflowId, todosV2.Version, ct);
        impact.Affected.Select(record => record.Version.Value).Should().BeEquivalentTo(
            "contracts/todos@1",
            "components/todos@1",
            "execution-graph/main@1",
            "bindings/main@1");
        (await artifactCatalog.GetAsync(WorkflowId, versions.Contracts["Reporting"], ct))!
            .State.Should().Be(PlanningArtifactState.Valid);
        (await artifactCatalog.GetAsync(WorkflowId, versions.Components["Reporting"], ct))!
            .State.Should().Be(PlanningArtifactState.Valid);

        // Regenerate the affected design: same graph shape, but the Todos
        // implementation now binds the revised task descriptor.
        var revisedContracts = new List<BoundedContextContractCatalog>(
            artifacts.ContractCatalogs.Select(catalog =>
                catalog.BoundedContextName == "Todos" ? revisedTodos : catalog));
        var revisedArtifacts = new StagedPlanningArtifacts(
            artifacts.Domain, artifacts.Topology, revisedContracts, artifacts.ComponentManifests);
        var revisedVersions = new StagedPlanningArtifactVersions(
            versions.Domain,
            versions.Topology,
            new Dictionary<string, PlanningArtifactVersion>(StringComparer.Ordinal)
            {
                ["Todos"] = todosV2.Version,
                ["Reporting"] = versions.Contracts["Reporting"],
            },
            versions.Components);
        var revisedDesign = StagedExecutionGraphBuilder.Build(
            revisedArtifacts, revisedVersions, TrustedByRole("sample.task2", 'e', 'c', 'd'));
        revisedDesign.Bindings.Nodes
            .Single(node => node.StepId == "implement_todos").Binding.Descriptor.ContentDigest.Value
            .Should().Be(Digest('e'));

        // v3: regenerate the affected Fuwen nodes and mutate again.
        var admission3 = await AuthorAsync(revisedDesign, V3Dsl(), ct);
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
            v2Id, "plan/implement_todos",
            new ForkRunOptions { TargetWorkflowVersion = "3", Actor = "test", Reason = "todos contract changed" },
            ct);
        var v3 = await engine.GetRunAsync(v3Id, ct);
        v3!.WorkflowVersion.Should().Be("3");
        v3.SourceRunId.Should().Be(v2Id);
        await engine.ExecuteAsync(v3Id, ct);
        var output = await engine.WaitForCompletionAsync<JsonElement>(v3Id, cancellationToken: ct);

        // Exactly the affected subgraph reran; the Reporting branch was preserved.
        CallsFor(activity, "implement_todos").Should().HaveCount(2);
        CallsFor(activity, "implement_reporting").Should().HaveCount(1);
        CallsFor(activity, "integrate").Should().HaveCount(2);
        CallsFor(activity, "test").Should().HaveCount(2);
        CallsFor(activity, "domain").Should().HaveCount(1);
        (await engine.GetRunAsync(v3Id, ct))!.Status.Should().Be(WorkflowStatus.Completed);
        output.GetString().Should().NotBeNullOrWhiteSpace();
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
        string implementName, char implementDigest, char integrateDigest, char verifyDigest) =>
        new(StringComparer.Ordinal)
        {
            [PlannedExecutionRoles.Implement] = ActivityDescriptor(implementName, implementDigest, "value"),
            [PlannedExecutionRoles.Integrate] = ActivityDescriptor("sample.join", integrateDigest, "first", "second"),
            [PlannedExecutionRoles.Verify] = ActivityDescriptor("sample.check", verifyDigest, "value"),
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
        PlannedExecutionDesign design, string dsl, CancellationToken ct)
    {
        var router = new CannedPlanRouter(dsl);
        var author = new WorkflowAuthor(
            router,
            new WorkflowAuthoringPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()))),
            maxAttempts: 3);
        var result = await author.AuthorFromPlanAsync(
            "Implement ticket classification.",
            PlannedExecutionDesignSummary.Render(design),
            CatalogueSummaryBuilder.Render(Descriptors()),
            Catalogue(),
            "stub-author",
            4000,
            ct);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Admission.Should().NotBeNull();
        return result.Admission!;
    }

    private static (DomainDiscovery Domain, SolutionTopology Topology,
        List<BoundedContextContractCatalog> Contracts,
        List<BoundedContextComponentManifest> Components) CreateStagedPayloads()
    {
        var domain = new DomainDiscovery
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
        };
        var topology = new SolutionTopology
        {
            Solution = new PlannedSolution { Name = "Support", Path = "Support.sln" },
            Projects = [],
            BoundedContexts =
            [
                new BoundedContextPlan
                {
                    Name = "Todos",
                    Purpose = "Manage todos.",
                    CapabilityNames = [],
                    DependsOnContextNames = [],
                    InboundAdapters = [],
                    OutboundAdapters = [],
                },
                new BoundedContextPlan
                {
                    Name = "Reporting",
                    Purpose = "Report on todos.",
                    CapabilityNames = [],
                    DependsOnContextNames = [],
                    InboundAdapters = [],
                    OutboundAdapters = [],
                },
            ],
            Modules = [],
            Decisions = [],
        };
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
        return (domain, topology, contracts, components);
    }

    private static IReadOnlyList<string> CallsFor(TaskActivity activity, string node) =>
        activity.Calls.Where(call => call.StartsWith(node + ":", StringComparison.Ordinal)).ToArray();

    private static string FindPromptsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "prompts", "workflow-authoring", "system.sbn");
            if (File.Exists(candidate))
                return Path.Combine(directory.FullName, "prompts");
        }
        throw new DirectoryNotFoundException("Could not locate the Guyabano prompts root.");
    }

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

