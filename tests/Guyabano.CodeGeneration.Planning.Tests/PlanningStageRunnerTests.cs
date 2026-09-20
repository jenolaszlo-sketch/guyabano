using System.Text.Json;
using FluentAssertions;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class PlanningStageRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-stage-runner-tests",
        Guid.NewGuid().ToString("N"));

    private static PlanningStageCatalogue ResearchCatalogue() => PlanningStageCatalogue.Create(
    [
        new PlanningStageDefinition
        {
            Id = "research",
            ArtifactKind = "research-notes",
            SystemPack = "research/system.sbn",
            UserPack = "research/user.sbn",
            InputKinds = [],
            OutputSchema = "ResearchNotes",
            MaxAttempts = 2,
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
            MaxAttempts = 2,
            ModelProfile = "verification",
        },
    ]);

    private static PlanningStagePlan ResearchPlan(PlanningStageCatalogue catalogue) => new()
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
        Rationale = "Understand the problem before measuring it.",
        Provenance = new PlanningStagePlanProvenance
        {
            ProducedBy = "test",
            InputRevisions = [],
            DefinitionCatalogueVersion = catalogue.Version,
        },
    };

    private static JsonElement Payload(string text) =>
        JsonSerializer.SerializeToElement(new { note = text });

    [Fact]
    public async Task Run_executes_a_custom_path_and_publishes_revisions()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogue = ResearchCatalogue();
        var catalog = new PlanningArtifactCatalog(new FileSystemArtifactRepository(_root));
        var seen = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>();
        var runner = new PlanningStageRunner(
            catalogue,
            new Dictionary<string, IPlanningStageExecutor>(StringComparer.Ordinal)
            {
                ["research"] = new ScriptedExecutor("research", (_, _) => Payload("found-it")),
                ["benchmark"] = new ScriptedExecutor("benchmark", (input, _) =>
                {
                    seen["benchmark"] = input.UpstreamOutputs;
                    return Payload("measured");
                }),
            },
            catalog);

        var result = await runner.RunAsync(
            "workflow-1", ResearchPlan(catalogue), new HashSet<string>(), ct);

        result.Succeeded.Should().BeTrue(string.Join(" ", result.Diagnostics));
        result.Published.Should().Equal("research-notes/main@1", "benchmarks/main@1");
        seen["benchmark"].Keys.Should().ContainSingle().Which.Should().Be("research-notes/main");
        var stored = await catalog.ReadPayloadAsync<JsonElement>(
            (await catalog.GetCurrentAsync(
                "workflow-1", new PlanningArtifactKey("benchmarks", "main"), ct))!, ct);
        stored.GetProperty("note").GetString().Should().Be("measured");
    }

    [Fact]
    public async Task Run_rejects_unknown_stage_ids()
    {
        var catalogue = ResearchCatalogue();
        var plan = ResearchPlan(catalogue) with
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

        var errors = PlanningStageValidator.Validate(plan, catalogue, new HashSet<string>());

        errors.Should().ContainSingle().Which.Should().Contain("unknown stage id");
    }

    [Fact]
    public async Task Run_rejects_cycles_dangling_edges_and_kind_mismatches()
    {
        var catalogue = ResearchCatalogue();
        var cyclic = ResearchPlan(catalogue) with
        {
            Stages =
            [
                new PlannedStage
                {
                    StageId = "research",
                    Name = "research-notes/main",
                    DependsOn = ["benchmarks/main"],
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
        };
        PlanningStageValidator.Validate(cyclic, catalogue, new HashSet<string>())
            .Should().Contain(error => error.Contains("cycle"));

        var dangling = ResearchPlan(catalogue) with
        {
            Stages =
            [
                new PlannedStage
                {
                    StageId = "benchmark",
                    Name = "benchmarks/main",
                    DependsOn = ["research-notes/missing"],
                    InputArtifacts = [],
                },
            ],
        };
        PlanningStageValidator.Validate(dangling, catalogue, new HashSet<string>())
            .Should().Contain(error => error.Contains("unknown instance"));

        var mismatched = ResearchPlan(catalogue) with
        {
            Stages =
            [
                new PlannedStage
                {
                    StageId = "research",
                    Name = "benchmarks/main",
                    DependsOn = [],
                    InputArtifacts = [],
                },
            ],
        };
        PlanningStageValidator.Validate(mismatched, catalogue, new HashSet<string>())
            .Should().Contain(error => error.Contains("has kind"));
    }

    [Fact]
    public async Task Run_rejects_stale_catalogue_versions()
    {
        var catalogue = ResearchCatalogue();
        var plan = ResearchPlan(catalogue) with
        {
            Provenance = new PlanningStagePlanProvenance
            {
                ProducedBy = "test",
                InputRevisions = [],
                DefinitionCatalogueVersion = "sha256:planning-stages/v1:stale",
            },
        };

        var errors = PlanningStageValidator.Validate(plan, catalogue, new HashSet<string>());

        errors.Should().ContainSingle().Which.Should().Contain("stale");
    }

    [Fact]
    public async Task Run_stops_at_the_first_failing_stage()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogue = ResearchCatalogue();
        var catalog = new PlanningArtifactCatalog(new FileSystemArtifactRepository(_root));
        var runner = new PlanningStageRunner(
            catalogue,
            new Dictionary<string, IPlanningStageExecutor>(StringComparer.Ordinal)
            {
                ["research"] = new ScriptedExecutor("research", (_, _) => Payload("found-it")),
                ["benchmark"] = new FailingExecutor("benchmark"),
            },
            catalog);

        var result = await runner.RunAsync(
            "workflow-1", ResearchPlan(catalogue), new HashSet<string>(), ct);

        result.Succeeded.Should().BeFalse();
        result.Outputs.Keys.Should().ContainSingle().Which.Should().Be("research-notes/main");
        result.Published.Should().ContainSingle().Which.Should().Be("research-notes/main@1");
        result.Diagnostics.Should().ContainSingle().Which.Should().Contain("benchmarks/main");
    }

    [Fact]
    public void Built_in_catalogue_mirrors_the_current_pipeline()
    {
        var catalogue = BuiltInPlanningStages.Catalogue();

        catalogue.Definitions.Select(definition => definition.Id).Should().Equal(
            "component-design", "contract-design", "domain-discovery", "solution-topology");
        var byId = catalogue.Definitions.ToDictionary(definition => definition.Id);
        byId["domain-discovery"].ArtifactKind.Should().Be("domain-discovery");
        byId["domain-discovery"].InputKinds.Should().BeEmpty();
        byId["solution-topology"].InputKinds.Should().Equal("domain-discovery");
        byId["contract-design"].ArtifactKind.Should().Be("contracts");
        byId["contract-design"].InputKinds.Should()
            .BeEquivalentTo("domain-discovery", "solution-topology", "contracts");
        byId["component-design"].ArtifactKind.Should().Be("components");
        foreach (var definition in catalogue.Definitions)
        {
            definition.SystemPack.Should().Be($"{definition.Id}/system.sbn");
            definition.UserPack.Should().Be($"{definition.Id}/user.sbn");
            definition.MaxAttempts.Should().Be(3);
            definition.ModelProfile.Should().Be(definition.Id);
        }

        catalogue.Version.Should().StartWith(PlanningStageCatalogue.VersionScheme);
        BuiltInPlanningStages.Catalogue().Version.Should().Be(catalogue.Version);
    }

    [Fact]
    public void Catalogue_rejects_duplicate_ids()
    {
        var action = () => PlanningStageCatalogue.Create(
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
                Id = "research",
                ArtifactKind = "other",
                SystemPack = "other/system.sbn",
                UserPack = "other/user.sbn",
                InputKinds = [],
                OutputSchema = "Other",
                MaxAttempts = 1,
                ModelProfile = "other",
            },
        ]);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate ids*research*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class ScriptedExecutor(
        string stageId,
        Func<StageExecutionInput, CancellationToken, JsonElement> produce) : IPlanningStageExecutor
    {
        public string StageId => stageId;

        public Task<StageExecutionResult> ExecuteAsync(
            StageExecutionInput input,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageExecutionResult(true, produce(input, cancellationToken), []));
    }

    private sealed class FailingExecutor(string stageId) : IPlanningStageExecutor
    {
        public string StageId => stageId;

        public Task<StageExecutionResult> ExecuteAsync(
            StageExecutionInput input,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageExecutionResult(false, null, ["benchmark unavailable"]));
    }
}
