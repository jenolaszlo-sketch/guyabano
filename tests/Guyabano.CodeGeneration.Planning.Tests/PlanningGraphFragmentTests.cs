using System.Text.Json;
using FluentAssertions;
using Guyabano.Artifacts;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class PlanningGraphFragmentTests
{
    private static ContentDigest Digest(char c) => new("sha256", "descriptor/v1", new string(c, 64));

    private static PlanningGraphFragmentInput ResearchInput(params string[] names) => new(
        "research-notes",
        names.Select((name, index) => new FragmentArtifact(
                new PlanningArtifactVersion(new PlanningArtifactKey("research-notes", name), index + 1),
                JsonSerializer.SerializeToElement(new { topic = name })))
            .ToArray(),
        new DescriptorReference(DescriptorKind.Activity, "guyabano.research", "1", Digest('r')));

    private static PlanningGraphFragmentInput BenchmarkInput(params string[] names) => new(
        "benchmarks",
        names.Select((name, index) => new FragmentArtifact(
                new PlanningArtifactVersion(new PlanningArtifactKey("benchmarks", name), index + 1),
                JsonSerializer.SerializeToElement(new { topic = name })))
            .ToArray(),
        new DescriptorReference(DescriptorKind.Activity, "guyabano.measure", "1", Digest('m')));

    [Fact]
    public void Assemble_derives_steps_from_each_registered_kind()
    {
        var result = PlanningGraphFragmentAssembler.Assemble(
            "research-run",
            "string",
            "string",
            [ResearchInput("caching"), BenchmarkInput("caching")],
            new Dictionary<string, IPlanningGraphFragmentBuilder>(StringComparer.Ordinal)
            {
                ["research-notes"] = new ResearchFragmentBuilder(),
                ["benchmarks"] = new BenchmarkFragmentBuilder(),
            });

        result.SkippedKinds.Should().BeEmpty();
        result.Design.Graph.Steps.Select(step => step.Id).Should()
            .Equal("research_caching", "measure_caching");
        result.Design.Graph.Steps.Single(step => step.Id == "measure_caching")
            .DependsOn.Should().Equal("research_caching");
        result.Design.Bindings.Nodes.Select(node => node.StepId).Should()
            .BeEquivalentTo("research_caching", "measure_caching");
        result.Design.Bindings.Nodes.Single(node => node.StepId == "research_caching")
            .Binding.Descriptor.Name.Should().Be("guyabano.research");
    }

    [Fact]
    public void Assemble_skips_kinds_without_builders_for_explicit_patching()
    {
        var result = PlanningGraphFragmentAssembler.Assemble(
            "research-run",
            "string",
            "string",
            [ResearchInput("caching"), BenchmarkInput("caching")],
            new Dictionary<string, IPlanningGraphFragmentBuilder>(StringComparer.Ordinal)
            {
                ["research-notes"] = new ResearchFragmentBuilder(),
            });

        result.SkippedKinds.Should().ContainSingle().Which.Should().Be("benchmarks");
        result.Design.Graph.Steps.Select(step => step.Id).Should().Equal("research_caching");
    }

    [Fact]
    public void Assemble_rejects_duplicate_ids_missing_bindings_and_cycles()
    {
        var builders = new Dictionary<string, IPlanningGraphFragmentBuilder>(StringComparer.Ordinal)
        {
            ["research-notes"] = new ResearchFragmentBuilder(),
        };
        var duplicate = () => PlanningGraphFragmentAssembler.Assemble(
            "run", "string", "string",
            [ResearchInput("caching"), ResearchInput("caching")], builders);
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*");

        var missing = () => PlanningGraphFragmentAssembler.Assemble(
            "run", "string", "string",
            [new PlanningGraphFragmentInput(
                "research-notes",
                ResearchInput("caching").Artifacts,
                ResearchInput("caching").Descriptor)],
            new Dictionary<string, IPlanningGraphFragmentBuilder>(StringComparer.Ordinal)
            {
                ["research-notes"] = new BindinglessFragmentBuilder(),
            });
        missing.Should().Throw<InvalidOperationException>().WithMessage("*no binding*");

        var cyclic = () => PlanningGraphFragmentAssembler.Assemble(
            "run", "string", "string",
            [ResearchInput("caching")],
            new Dictionary<string, IPlanningGraphFragmentBuilder>(StringComparer.Ordinal)
            {
                ["research-notes"] = new CyclicFragmentBuilder(),
            });
        cyclic.Should().Throw<InvalidOperationException>().WithMessage("*cycle*");
    }

    private sealed class ResearchFragmentBuilder : IPlanningGraphFragmentBuilder
    {
        public string ArtifactKind => "research-notes";

        public PlanningGraphFragment BuildFragment(PlanningGraphFragmentInput input)
        {
            var steps = new List<PlannedExecutionStep>();
            var bindings = new List<PlannedNodeBinding>();
            foreach (var artifact in input.Artifacts)
            {
                var id = $"research_{artifact.Version.Key.Name}";
                steps.Add(new PlannedExecutionStep
                {
                    Id = id,
                    Title = $"Research {artifact.Version.Key.Name}",
                    DependsOn = [],
                    RequiredArtifacts = [artifact.Version.Value],
                    AcceptanceCriteria = [],
                });
                bindings.Add(new PlannedNodeBinding
                {
                    StepId = id,
                    Binding = new PlannedExecutionBinding
                    {
                        Role = "research",
                        Capability = "research.read",
                        ModelProfile = "research",
                        ContextArtifacts = [artifact.Version.Value],
                        Descriptor = input.Descriptor,
                    },
                });
            }

            return new PlanningGraphFragment(steps, bindings);
        }
    }

    private sealed class BenchmarkFragmentBuilder : IPlanningGraphFragmentBuilder
    {
        public string ArtifactKind => "benchmarks";

        public PlanningGraphFragment BuildFragment(PlanningGraphFragmentInput input)
        {
            var steps = new List<PlannedExecutionStep>();
            var bindings = new List<PlannedNodeBinding>();
            foreach (var artifact in input.Artifacts)
            {
                var id = $"measure_{artifact.Version.Key.Name}";
                steps.Add(new PlannedExecutionStep
                {
                    Id = id,
                    Title = $"Measure {artifact.Version.Key.Name}",
                    DependsOn = [$"research_{artifact.Version.Key.Name}"],
                    RequiredArtifacts = [artifact.Version.Value],
                    AcceptanceCriteria = [],
                });
                bindings.Add(new PlannedNodeBinding
                {
                    StepId = id,
                    Binding = new PlannedExecutionBinding
                    {
                        Role = "verify",
                        Capability = "process.execute",
                        ModelProfile = "verification",
                        ContextArtifacts = [artifact.Version.Value],
                        Descriptor = input.Descriptor,
                    },
                });
            }

            return new PlanningGraphFragment(steps, bindings);
        }
    }

    private sealed class BindinglessFragmentBuilder : IPlanningGraphFragmentBuilder
    {
        public string ArtifactKind => "research-notes";

        public PlanningGraphFragment BuildFragment(PlanningGraphFragmentInput input) => new(
            [
                new PlannedExecutionStep
                {
                    Id = "lonely",
                    Title = "Lonely",
                    DependsOn = [],
                    RequiredArtifacts = [],
                    AcceptanceCriteria = [],
                },
            ],
            []);
    }

    private sealed class CyclicFragmentBuilder : IPlanningGraphFragmentBuilder
    {
        public string ArtifactKind => "research-notes";

        public PlanningGraphFragment BuildFragment(PlanningGraphFragmentInput input) => new(
            [
                new PlannedExecutionStep
                {
                    Id = "a",
                    Title = "A",
                    DependsOn = ["b"],
                    RequiredArtifacts = [],
                    AcceptanceCriteria = [],
                },
                new PlannedExecutionStep
                {
                    Id = "b",
                    Title = "B",
                    DependsOn = ["a"],
                    RequiredArtifacts = [],
                    AcceptanceCriteria = [],
                },
            ],
            [
                new PlannedNodeBinding
                {
                    StepId = "a",
                    Binding = Binding("a"),
                },
                new PlannedNodeBinding
                {
                    StepId = "b",
                    Binding = Binding("b"),
                },
            ]);

        private static PlannedExecutionBinding Binding(string step) => new()
        {
            Role = "research",
            Capability = "research.read",
            ModelProfile = "research",
            ContextArtifacts = [],
            Descriptor = new DescriptorReference(
                DescriptorKind.Activity, "guyabano.research", "1", Digest('r')),
        };
    }
}
