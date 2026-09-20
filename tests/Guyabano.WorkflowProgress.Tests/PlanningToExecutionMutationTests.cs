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
/// Spec §20 Test 1 (planning-to-execution mutation): a planning-only workflow
/// produces cataloged artifacts; an execution design is authored into Fuwen
/// from those artifacts; the workflow forks to the implementation version.
/// Planning nodes stay completed, planning artifacts remain available, only
/// the new execution nodes run, and lineage records both versions.
/// </summary>
public sealed class PlanningToExecutionMutationTests : IDisposable
{
    private const string WorkflowId = "plan";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-planning-mutation",
        Guid.NewGuid().ToString("N"));

    private static string Digest(char c) => new string(c, 64);

    private static TrustedCatalogueDescriptor StageDescriptor(string name, char digest)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new TrustedCatalogueDescriptor(
            new DescriptorReference(DescriptorKind.Activity, name, "1",
                new ContentDigest("sha256", "descriptor/v1", Digest(digest))),
            callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("value", str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe));
    }

    private static ITrustedCatalogue Catalogue() =>
        new InMemoryTrustedCatalogue(Descriptors());

    private static TrustedCatalogueDescriptor[] Descriptors() =>
    [
        StageDescriptor("sample.stage", 'a'),
        StageDescriptor("sample.task", 'b'),
        StageDescriptor("sample.join", 'c'),
        StageDescriptor("sample.check", 'd'),
    ];

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
          activity implement_billing = activity "sample.task@1#{{Digest('b')}}" (value: implement_todos;) -> string;
          activity integrate = activity "sample.join@1#{{Digest('c')}}" (value: implement_billing;) -> string;
          activity test = activity "sample.check@1#{{Digest('d')}}" (value: integrate;) -> string;
          return test;
        }
        """;

    [Fact]
    public async Task Planning_workflow_mutates_into_implementation_preserving_completed_planning()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogue = Catalogue();
        var staged = CreateStagedPayloads();
        var activity = new PlanningTaskActivity(new Dictionary<string, string>(StringComparer.Ordinal)
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
        var compiler = new WorkflowCompiler(
            catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", []));

        // v1: planning-only workflow (admit the exact DSL the stages produce).
        var admission1 = await AdmitDslAsync(V1Dsl(), catalogue, compiler, ct);
        var identity = new FuwenZhinuProviderRuntimeIdentity(
            admission1.Receipt!.CatalogueSnapshotRevision,
            admission1.Receipt.ResolvedDescriptorSetFingerprint);
        var registration1 = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(), identity,
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync(WorkflowId, "1", admission1, ct);
        registration1.Register(registry);
        await using var engine = new WorkflowEngine(store, registry,
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

        using var input = JsonDocument.Parse("\"goal\"");
        var sourceId = await engine.StartAsync(
            WorkflowId, "1", input.RootElement.Clone(), cancellationToken: ct);
        await engine.ExecuteAsync(sourceId, ct);
        await engine.WaitForCompletionAsync<JsonElement>(sourceId, cancellationToken: ct);
        activity.Calls.Should().Equal(
            "domain:goal",
            $"topology:{activity.Outputs["domain"]}",
            $"contracts:{activity.Outputs["topology"]}",
            $"components:{activity.Outputs["contracts"]}");

        // Publish the planning cascade and derive the execution design from it.
        var artifactRepository = new FileSystemArtifactRepository(Path.Combine(_root, "artifacts"));
        var artifactCatalog = new PlanningArtifactCatalog(artifactRepository);
        var publisher = new StagedPlanningArtifactPublisher(artifactCatalog);
        var artifacts = new StagedPlanningArtifacts(
            JsonSerializer.Deserialize<DomainDiscovery>(activity.Outputs["domain"])!,
            JsonSerializer.Deserialize<SolutionTopology>(activity.Outputs["topology"])!,
            JsonSerializer.Deserialize<List<BoundedContextContractCatalog>>(activity.Outputs["contracts"])!,
            JsonSerializer.Deserialize<List<BoundedContextComponentManifest>>(activity.Outputs["components"])!);
        var versions = await publisher.PublishAsync(WorkflowId, artifacts, cancellationToken: ct);
        var trustedByRole = new Dictionary<string, TrustedCatalogueDescriptor>(StringComparer.Ordinal)
        {
            [PlannedExecutionRoles.Implement] = StageDescriptor("sample.task", 'b'),
            [PlannedExecutionRoles.Integrate] = StageDescriptor("sample.join", 'c'),
            [PlannedExecutionRoles.Verify] = StageDescriptor("sample.check", 'd'),
        };
        var design = StagedExecutionGraphBuilder.Build(artifacts, versions, trustedByRole);
        await publisher.PublishExecutionDesignAsync(
            WorkflowId,
            design,
            [.. versions.Contracts.Values, .. versions.Components.Values],
            cancellationToken: ct);

        // Author v2 from the resolved design (canned model) and register it.
        var router = new CannedPlanRouter(V2Dsl());
        var author = new WorkflowAuthor(
            router,
            new WorkflowAuthoringPromptBuilder(
                new ScribanPromptTemplateEngine(new EmbeddedPromptLoader())),
            catalogue,
            maxAttempts: 3);
        var authored = await author.AuthorFromPlanAsync(
            "Implement ticket classification.",
            PlanningGraphSummary.Render(design),
            CatalogueSummaryBuilder.Render(Descriptors()),
            "stub-author",
            4000,
            ct);
        authored.Succeeded.Should().BeTrue(string.Join("; ", authored.Diagnostics));
        authored.Admission.Should().NotBeNull();
        var admission2 = authored.Admission!;
        var ports2 = new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference())
        {
            PriorExecutionFingerprints = new HashSet<string>(
                [admission1.Receipt.ExecutionFingerprint], StringComparer.Ordinal),
        };
        // v2 resolves a larger descriptor set than v1, so its provider
        // runtime identity comes from its own receipt.
        var identity2 = new FuwenZhinuProviderRuntimeIdentity(
            admission2.Receipt!.CatalogueSnapshotRevision,
            admission2.Receipt.ResolvedDescriptorSetFingerprint);
        var registration2 = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(), identity2, ports2)
            .CreateAsync(WorkflowId, "2", admission2, ct);
        registration2.Register(registry);

        // Mutate: fork at the first new execution node.
        var migratedId = await engine.ForkAsync(
            sourceId,
            "plan/implement_todos",
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

        // Only the new execution nodes ran; planning evidence was reused.
        activity.Calls.Should().Equal(
            "domain:goal",
            $"topology:{activity.Outputs["domain"]}",
            $"contracts:{activity.Outputs["topology"]}",
            $"components:{activity.Outputs["contracts"]}",
            $"implement_todos:{activity.Outputs["components"]}",
            $"implement_billing:{activity.Outputs["implement_todos"]}",
            $"integrate:{activity.Outputs["implement_billing"]}",
            $"test:{activity.Outputs["integrate"]}");
        output.GetString()!.Should().EndWith(".implement_todos.implement_billing.integrate.test");
        (await engine.GetRunAsync(migratedId, ct))!.Status.Should().Be(WorkflowStatus.Completed);

        // Planning artifacts remain available at their pinned revisions.
        foreach (var version in versions.Contracts.Values.Concat(versions.Components.Values)
                     .Prepend(versions.Domain).Prepend(versions.Topology))
        {
            var record = await artifactCatalog.GetAsync(WorkflowId, version, ct);
            record.Should().NotBeNull();
        }
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
                    Name = "Billing",
                    Purpose = "Bill for todos.",
                    CapabilityNames = [],
                    DependsOnContextNames = ["Todos"],
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
            new() { BoundedContextName = "Billing", Contracts = [], Decisions = [], InferredDefaults = [] },
        ];
        List<BoundedContextComponentManifest> components =
        [
            new() { BoundedContextName = "Todos", Components = [], Decisions = [], InferredDefaults = [] },
            new() { BoundedContextName = "Billing", Components = [], Decisions = [], InferredDefaults = [] },
        ];
        return (domain, topology, contracts, components);
    }

    private sealed class PlanningTaskActivity(Dictionary<string, string> canned) : IActivityExecutor
    {
        public List<string> Calls { get; } = [];
        public Dictionary<string, string> Outputs { get; } = new(StringComparer.Ordinal);

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var node = request.Invocation.StructuralPath.Split('/')[^1];
            var input = ((JsonRuntimeValue)request.Arguments.Single(a => a.Name == "value").Value)
                .Value.GetString()!;
            Calls.Add($"{node}:{input}");
            // Planning nodes return their stage payload as JSON text carried
            // in a string value, so the Fuwen string contract holds and the
            // test can deserialize the text into the staged artifact.
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
