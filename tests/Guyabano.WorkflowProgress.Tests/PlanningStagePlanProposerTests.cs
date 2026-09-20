#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.Llm.Prompting;
using Penghou.Baize;
using Penghou.Baize.Router;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Gap D bootstrap: a model proposes which planning artifacts an objective
/// requires as stage-plan JSON; provenance is stamped deterministically and
/// admission owns validity with repair feedback.
/// </summary>
public sealed class PlanningStagePlanProposerTests
{
    private static PlanningStageCatalogue Catalogue() => PlanningStageCatalogue.Create(
    [
        new PlanningStageDefinition
        {
            Id = "research",
            ArtifactKind = "research-notes",
            SystemPack = "research/system.sbn",
            UserPack = "research/user.sbn",
            InputKinds = [],
            OutputSchema = "ResearchNotes",
            MaxAttempts = 1,
            ModelProfile = "research",
        },
        new PlanningStageDefinition
        {
            Id = "benchmark",
            ArtifactKind = "benchmarks",
            SystemPack = "benchmark/system.sbn",
            UserPack = "benchmark/user.sbn",
            InputKinds = ["research-notes"],
            OutputSchema = "BenchmarkReport",
            MaxAttempts = 1,
            ModelProfile = "verification",
        },
    ]);

    private static PlanningStagePlan Plan() => new()
    {
        Stages =
        [
            new PlannedStage
            {
                StageId = "research",
                Name = "research-notes/main",
                DependsOn = [],
                InputArtifacts = [],
            },
            new PlannedStage
            {
                StageId = "benchmark",
                Name = "benchmarks/main",
                DependsOn = ["research-notes/main"],
                InputArtifacts = [],
            },
        ],
        Rationale = "Understand, then measure.",
        Provenance = new PlanningStagePlanProvenance
        {
            ProducedBy = "model-draft",
            InputRevisions = [],
            DefinitionCatalogueVersion = "model-draft",
        },
    };

    [Fact]
    public async Task Bootstrap_pack_renders_the_catalogue_and_revisions()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogue = Catalogue();

        var builder = new PlanningBootstrapPromptBuilder(
            new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot())));
        var request = await builder.BuildAsync(
            new PlanningBootstrapPromptContext(
                "Compare caching strategies.",
                PlanningStageCatalogueSummary.Render(catalogue),
                catalogue.Version,
                ["contracts/billing@2"],
                2000),
            ct);

        var systemText = TextOf(request, "system");
        var userText = TextOf(request, "user");
        systemText.Should().Contain("research produces research-notes");
        systemText.Should().Contain(catalogue.Version);
        systemText.Should().Contain("contracts/billing@2");
        userText.Should().Contain("Compare caching strategies.");
        userText.Should().Contain("exactly one JSON object");
    }

    [Fact]
    public async Task Propose_accepts_a_valid_plan_and_stamps_provenance()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogue = Catalogue();
        var router = new ScriptedRouter([JsonSerializer.Serialize(Plan())]);
        var proposer = new PlanningStagePlanProposer(
            router,
            new PlanningBootstrapPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()))),
            maxAttempts: 1);

        var result = await proposer.ProposeAsync(
            "Compare caching strategies.",
            catalogue,
            ["contracts/billing@2"],
            "stub-planner",
            2000,
            ct);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Attempts.Should().HaveCount(1);
        result.Plan!.Stages.Select(stage => stage.StageId).Should().Equal("research", "benchmark");
        result.Plan.Provenance.ProducedBy.Should().Be("llm:stub-planner");
        result.Plan.Provenance.InputRevisions.Should().Equal("contracts/billing@2");
        result.Plan.Provenance.DefinitionCatalogueVersion.Should().Be(catalogue.Version);
    }

    [Fact]
    public async Task Propose_repairs_an_unknown_stage_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogue = Catalogue();
        var bad = Plan() with
        {
            Stages =
            [
                new PlannedStage
                {
                    StageId = "nope",
                    Name = "nope/main",
                    DependsOn = [],
                    InputArtifacts = [],
                },
            ],
        };
        var router = new ScriptedRouter(
        [
            JsonSerializer.Serialize(bad),
            JsonSerializer.Serialize(Plan()),
        ]);
        var proposer = new PlanningStagePlanProposer(
            router,
            new PlanningBootstrapPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()))),
            maxAttempts: 2);

        var result = await proposer.ProposeAsync(
            "Compare caching strategies.",
            catalogue,
            [],
            "stub-planner",
            2000,
            ct);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Attempts.Should().HaveCount(2);
        result.Attempts[0].Diagnostics.Should().ContainSingle()
            .Which.Should().Contain("unknown stage id");
        TextOf(router.Requests[1], "user").Should().Contain("unknown stage id");
    }

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
            var candidate = Path.Combine(directory.FullName, "prompts", "planning-bootstrap", "system.sbn");
            if (File.Exists(candidate))
                return Path.Combine(directory.FullName, "prompts");
        }
        throw new DirectoryNotFoundException("Could not locate the Guyabano prompts root.");
    }

    private sealed class ScriptedRouter(IReadOnlyList<string> script) : ILlmRouter
    {
        private int next;
        public List<LlmRequest> Requests { get; } = [];

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
