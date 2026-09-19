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

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Phase 2 of model-authored workflows: the author translates a resolved
/// execution design instead of rediscovering architecture. A scripted model
/// emits the plan, and the compiler/admission verify its structure.
/// </summary>
public sealed class AuthorFromPlanTests : IDisposable
{
    private const string WorkflowId = "planning-workflow-1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-author-from-plan-tests",
        Guid.NewGuid().ToString("N"));

    private static string Digest(char c) => new string(c, 64);

    private static TrustedCatalogueDescriptor ActivityDescriptor(string name, string parameter, char digest)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new TrustedCatalogueDescriptor(
            new DescriptorReference(DescriptorKind.Activity, name, "1",
                new ContentDigest("sha256", "descriptor/v1", Digest(digest))),
            callableContract: new CallableContract(
                new CallableSignature([new CallableParameter(parameter, str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe));
    }

    private static IReadOnlyDictionary<string, TrustedCatalogueDescriptor> Registry() =>
        new Dictionary<string, TrustedCatalogueDescriptor>(StringComparer.Ordinal)
        {
            [PlannedExecutionRoles.Implement] = ActivityDescriptor("guyabano.generate", "task", 'f'),
            [PlannedExecutionRoles.Integrate] = ActivityDescriptor("guyabano.scaffold", "plan", 'd'),
            [PlannedExecutionRoles.Verify] = ActivityDescriptor("guyabano.build", "artifact", 'e'),
        };

    private static string TranslationDsl() => $$"""
        workflow implementation(input: string) -> string {
          activity implement_todos = activity "guyabano.generate@1#{{Digest('f')}}" (task: input;) -> string;
          activity implement_billing = activity "guyabano.generate@1#{{Digest('f')}}" (task: implement_todos;) -> string;
          activity integrate = activity "guyabano.scaffold@1#{{Digest('d')}}" (plan: implement_billing;) -> string;
          activity test = activity "guyabano.build@1#{{Digest('e')}}" (artifact: integrate;) -> string;
          return test;
        }
        """;

    private static TrustedCatalogueDescriptor GenerateV2Descriptor()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new TrustedCatalogueDescriptor(
            new DescriptorReference(DescriptorKind.Activity, "guyabano.generate", "2",
                new ContentDigest("sha256", "descriptor/v1", Digest('2'))),
            callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("task", str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe));
    }

    private static string BillingV2Ref() => $"guyabano.generate@2#{Digest('2')}";

    private static string PatchedDsl() => TranslationDsl().Replace(
        $"guyabano.generate@1#{Digest('f')}\" (task: implement_todos;)",
        $"{BillingV2Ref()}\" (task: implement_todos;)",
        StringComparison.Ordinal);

    private static string DriftedDsl() => PatchedDsl().Replace(
        "(task: input;)", "(task: \"classify\";)", StringComparison.Ordinal);

    [Fact]
    public async Task Authoring_pack_renders_the_resolved_execution_plan()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = await BuildDesignAsync(ct);
        var executionPlan = PlannedExecutionDesignSummary.Render(design);
        var catalogueSummary = CatalogueSummaryBuilder.Render(Registry().Values);

        var promptsRoot = FindPromptsRoot();
        var authorBuilder = new WorkflowAuthoringPromptBuilder(
            new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot)));
        var request = await authorBuilder.BuildAsync(
            new WorkflowAuthoringPromptContext(
                "Implement ticket classification.",
                catalogueSummary,
                4000,
                ExecutionPlan: executionPlan),
            ct);

        var systemText = TextOf(request, "system");
        var userText = TextOf(request, "user");
        systemText.Should().Contain("Resolved execution plan");
        systemText.Should().Contain("implement_todos");
        systemText.Should().Contain("implement_billing");
        systemText.Should().Contain($"guyabano.generate@1#{Digest('f')}");
        systemText.Should().Contain("contracts/billing@1");
        systemText.Should().Contain("do not add steps");
        systemText.Should().Contain("prompt <name>");
        systemText.Should().Contain("toolset <name>");
        systemText.Should().Contain("never both and never neither");
        systemText.Should().Contain("no model-callable tools");
        systemText.Should().Contain("reproduce every node for an unchanged step verbatim");
        userText.Should().Contain("preserve every step");
    }

    [Fact]
    public async Task Author_from_plan_admits_a_translation_with_matching_structure()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = await BuildDesignAsync(ct);
        var executionPlan = PlannedExecutionDesignSummary.Render(design);
        var registry = Registry();
        var catalogue = new InMemoryTrustedCatalogue(registry.Values);
        var catalogueSummary = CatalogueSummaryBuilder.Render(registry.Values);

        var promptsRoot = FindPromptsRoot();
        var router = new CannedPlanRouter(TranslationDsl());
        var author = new WorkflowAuthor(
            router,
            new WorkflowAuthoringPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot))),
            maxAttempts: 3);

        var result = await author.AuthorFromPlanAsync(
            "Implement ticket classification.",
            executionPlan,
            catalogueSummary,
            catalogue,
            "stub-author",
            4000,
            ct);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Admission.Should().NotBeNull();

        // The admitted plan mirrors the resolved design: the same node ids,
        // the same descriptor bindings, and argument edges that follow the
        // resolved dependencies. The stored node list is alphabetical, not
        // execution order, so structure is asserted through edges.
        var definition = result.Admission!.Compilation.Definition;
        definition.Should().NotBeNull();
        var nodes = definition!.ReadPlan().Nodes
            .OfType<ActivityNode>()
            .ToArray();
        nodes.Select(node => node.Name).Should().BeEquivalentTo(
            design.Graph.Steps.Select(step => step.Id));
        var bindingByStep = design.Bindings.Nodes.ToDictionary(node => node.StepId);
        var byName = nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        foreach (var step in design.Graph.Steps)
        {
            var node = byName[step.Id];
            node.Activity.ContentDigest.Value.Should().Be(
                bindingByStep[step.Id].Binding.Descriptor.ContentDigest.Value);
            var referenced = node.Arguments
                .Select(argument => argument.Value)
                .OfType<NodeOutputBinding>()
                .Select(binding => binding.NodePath.Split('/')[^1])
                .ToHashSet(StringComparer.Ordinal);
            foreach (var dependency in step.DependsOn)
            {
                // A node threaded on strings carries its first dependency;
                // the join node carries the most recent upstream output.
                if (step.DependsOn.Count == 1)
                    referenced.Should().Contain(dependency);
            }
            if (step.DependsOn.Count > 1)
                referenced.Should().NotBeEmpty();
            if (step.DependsOn.Count == 0)
                node.Arguments.Select(argument => argument.Value)
                    .Should().ContainItemsAssignableTo<InputBinding>();
        }

        // The model saw the execution plan, not just the goal.
        var userText = TextOf(router.Requests[0], "user");
        userText.Should().Contain("resolved execution plan");
        userText.Should().Contain("Implement ticket classification.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Patch_pack_renders_the_prior_workflow_and_changed_steps()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = await BuildDesignAsync(ct);
        var executionPlan = PlannedExecutionDesignSummary.Render(design);
        var catalogueSummary = CatalogueSummaryBuilder.Render(Registry().Values);

        var promptsRoot = FindPromptsRoot();
        var authorBuilder = new WorkflowAuthoringPromptBuilder(
            new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot)));
        var request = await authorBuilder.BuildAsync(
            new WorkflowAuthoringPromptContext(
                "Implement ticket classification.",
                catalogueSummary,
                4000,
                ExecutionPlan: executionPlan,
                PriorDsl: TranslationDsl(),
                ChangedSteps: ["implement_billing"]),
            ct);

        var systemText = TextOf(request, "system");
        var userText = TextOf(request, "user");
        systemText.Should().Contain("Prior workflow revision");
        systemText.Should().Contain("implement_billing");
        systemText.Should().Contain("activity implement_todos");
        userText.Should().Contain("implement_billing");
        userText.Should().Contain("verbatim");
    }

    [Fact]
    public async Task Author_from_patch_repairs_drift_and_admits()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = await BuildDesignAsync(ct);
        var registry = Registry();
        var catalogue = new InMemoryTrustedCatalogue(
            [.. registry.Values, GenerateV2Descriptor()]);
        var catalogueSummary = CatalogueSummaryBuilder.Render(catalogue.Descriptors);

        var priorPlan = await CompileAsync(catalogue, TranslationDsl(), ct);
        var patch = BillingV2Patch(design);
        var merged = StagedExecutionGraphBuilder.Apply(design, patch);
        var executionPlan = PlannedExecutionDesignSummary.Render(merged);

        var promptsRoot = FindPromptsRoot();
        var router = new CannedPlanRouter([DriftedDsl(), PatchedDsl()]);
        var author = new WorkflowAuthor(
            router,
            new WorkflowAuthoringPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot))),
            maxAttempts: 3);

        var result = await author.AuthorFromPatchAsync(
            "Implement ticket classification.",
            executionPlan,
            TranslationDsl(),
            priorPlan,
            patch,
            catalogueSummary,
            catalogue,
            "stub-author",
            4000,
            ct);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Attempts.Should().HaveCount(2);
        result.Attempts[0].Diagnostics.Should().ContainSingle()
            .Which.Should().Contain("outside the patch scope");
        TextOf(router.Requests[1], "user").Should().Contain("outside the patch scope");
        var nodes = result.Admission!.Compilation.Definition!.ReadPlan().Nodes
            .OfType<ActivityNode>()
            .ToDictionary(node => node.Name, StringComparer.Ordinal);
        nodes["implement_billing"].Activity.Version.Should().Be("2");
        nodes["implement_todos"].Arguments.Select(argument => argument.Value)
            .Should().ContainItemsAssignableTo<InputBinding>();
    }

    [Fact]
    public async Task Author_from_patch_fails_when_drift_persists()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = await BuildDesignAsync(ct);
        var registry = Registry();
        var catalogue = new InMemoryTrustedCatalogue(
            [.. registry.Values, GenerateV2Descriptor()]);
        var catalogueSummary = CatalogueSummaryBuilder.Render(catalogue.Descriptors);

        var priorPlan = await CompileAsync(catalogue, TranslationDsl(), ct);
        var patch = BillingV2Patch(design);
        var merged = StagedExecutionGraphBuilder.Apply(design, patch);
        var executionPlan = PlannedExecutionDesignSummary.Render(merged);

        var promptsRoot = FindPromptsRoot();
        var router = new CannedPlanRouter([DriftedDsl(), DriftedDsl()]);
        var author = new WorkflowAuthor(
            router,
            new WorkflowAuthoringPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot))),
            maxAttempts: 2);

        var result = await author.AuthorFromPatchAsync(
            "Implement ticket classification.",
            executionPlan,
            TranslationDsl(),
            priorPlan,
            patch,
            catalogueSummary,
            catalogue,
            "stub-author",
            4000,
            ct);

        result.Succeeded.Should().BeFalse();
        result.Attempts.Should().HaveCount(2);
        result.Attempts.Should().OnlyContain(attempt => attempt.Admitted);
        result.Attempts.SelectMany(attempt => attempt.Diagnostics)
            .Should().OnlyContain(diagnostic => diagnostic.Contains("outside the patch scope"));
    }

    private static WorkflowPatch BillingV2Patch(PlannedExecutionDesign design)
    {
        var billing = design.Bindings.Nodes.Single(node => node.StepId == "implement_billing");
        var v2 = new DescriptorReference(
            DescriptorKind.Activity, "guyabano.generate", "2",
            new ContentDigest("sha256", "descriptor/v1", Digest('2')));
        return new WorkflowPatch
        {
            BaseDesignFingerprint = StagedExecutionDesignIdentity.Compute(design),
            DerivedFromArtifacts = ["contracts/billing@1"],
            Rationale = "Use the v2 generator for billing.",
            AddSteps = [],
            ReplaceSteps = [],
            RemoveStepIds = [],
            AddBindings = [],
            ReplaceBindings = [billing with { Binding = billing.Binding with { Descriptor = v2 } }],
            DependencyEdits = [],
        };
    }

    private static async Task<WorkflowPlan> CompileAsync(
        ITrustedCatalogue catalogue, string dsl, CancellationToken ct)
    {
        var compiled = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(dsl, cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(
            string.Join("; ", compiled.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        return compiled.Plan!;
    }

    private async Task<PlannedExecutionDesign> BuildDesignAsync(CancellationToken ct)
    {
        var catalog = new PlanningArtifactCatalog(new FileSystemArtifactRepository(_root));
        var publisher = new StagedPlanningArtifactPublisher(catalog);
        var versions = await publisher.PublishAsync(
            WorkflowId, CreateArtifacts(), cancellationToken: ct);
        return StagedExecutionGraphBuilder.Build(
            CreateArtifacts(), versions, Registry());
    }

    private static StagedPlanningArtifacts CreateArtifacts() =>
        new(
            new DomainDiscovery
            {
                Mission = new ProductMission
                {
                    GuidingIntent = "Track todos.",
                    SuccessOutcomes = [],
                    Constraints = [],
                    NonGoals = [],
                },
                Title = "Todo API",
                Summary = "Tracks todos.",
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
                Solution = new PlannedSolution { Name = "TodoApi", Path = "TodoApi.sln" },
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
            },
            [
                new BoundedContextContractCatalog
                {
                    BoundedContextName = "Todos",
                    Contracts = [],
                    Decisions = [],
                    InferredDefaults = [],
                },
                new BoundedContextContractCatalog
                {
                    BoundedContextName = "Billing",
                    Contracts = [],
                    Decisions = [],
                    InferredDefaults = [],
                },
            ],
            [
                new BoundedContextComponentManifest
                {
                    BoundedContextName = "Todos",
                    Components = [],
                    Decisions = [],
                    InferredDefaults = [],
                },
                new BoundedContextComponentManifest
                {
                    BoundedContextName = "Billing",
                    Components = [],
                    Decisions = [],
                    InferredDefaults = [],
                },
            ]);

    private static string TextOf(LlmRequest request, string role) => string.Concat(request.Messages
        .Where(m => m.Role == role)
        .SelectMany(m => m.Parts)
        .OfType<LlmTextContent>()
        .Select(p => p.Text));

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

    private sealed class CannedPlanRouter(IReadOnlyList<string> script) : ILlmRouter
    {
        private int next;
        public List<LlmRequest> Requests { get; } = [];

        public CannedPlanRouter(string dsl)
            : this([dsl])
        {
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var index = Math.Min(next, script.Count - 1);
            next++;
            return StreamSingle(script[index]);
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
