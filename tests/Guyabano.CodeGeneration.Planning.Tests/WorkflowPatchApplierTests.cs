using FluentAssertions;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class WorkflowPatchApplierTests
{
    [Fact]
    public void Apply_AddsAStepAndPreservesSurvivorsByteIdentical()
    {
        var current = CreateDesign();
        var patch = ValidPatch(current);
        var added = Step("implement_cache", "Implement cache", ["implement_a"]);
        patch = patch with
        {
            AddSteps = [added],
            AddBindings = [Bind("implement_cache", "implement")],
        };

        var result = StagedExecutionGraphBuilder.Apply(current, patch);

        result.Graph.Steps.Should().HaveCount(5);
        result.Graph.Steps.Should().Contain(added);
        result.Graph.Steps.Where(step => step.Id != "implement_cache")
            .Should().Equal(current.Graph.Steps);
        result.Bindings.Nodes.Should().HaveCount(5);
        StagedExecutionDesignIdentity.Compute(result)
            .Should().NotBe(StagedExecutionDesignIdentity.Compute(current));
    }

    [Fact]
    public void Apply_EmptyPatchIsANoOp()
    {
        var current = CreateDesign();

        var result = StagedExecutionGraphBuilder.Apply(current, ValidPatch(current));

        StagedExecutionDesignIdentity.Compute(result)
            .Should().Be(StagedExecutionDesignIdentity.Compute(current));
        result.Graph.Steps.Should().Equal(current.Graph.Steps);
    }

    [Fact]
    public void Apply_RejectsStaleBase()
    {
        var current = CreateDesign();
        var patch = ValidPatch(current) with
        {
            BaseDesignFingerprint = "sha256:execution-design/v1:stale",
        };

        var action = () => StagedExecutionGraphBuilder.Apply(current, patch);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*different execution design*stale*");
    }

    [Fact]
    public void Apply_RejectsRemovalWithASurvivingDependent()
    {
        var current = CreateDesign();
        var patch = ValidPatch(current) with { RemoveStepIds = ["implement_a"] };

        var action = () => StagedExecutionGraphBuilder.Apply(current, patch);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*depends on unknown step*implement_a*");
    }

    [Fact]
    public void Apply_RejectsAnIntroducedCycle()
    {
        var current = CreateDesign();
        var patch = ValidPatch(current) with
        {
            DependencyEdits =
            [
                new StepDependencyEdit
                {
                    StepId = "implement_a",
                    DependsOn = ["integrate"],
                },
            ],
        };

        var action = () => StagedExecutionGraphBuilder.Apply(current, patch);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*dependency cycle*");
    }

    [Fact]
    public void Apply_RejectsAdditionsThatCollideWithSurvivors()
    {
        var current = CreateDesign();
        var patch = ValidPatch(current) with
        {
            AddSteps = [Step("implement_a", "Duplicate", [])],
            AddBindings = [Bind("implement_a", "implement")],
        };

        var action = () => StagedExecutionGraphBuilder.Apply(current, patch);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*implement_a*already exists*");
    }

    [Fact]
    public void Apply_RejectsReplacementsOfUnknownSteps()
    {
        var current = CreateDesign();
        var patch = ValidPatch(current) with
        {
            ReplaceSteps = [Step("implement_missing", "Missing", [])],
        };

        var action = () => StagedExecutionGraphBuilder.Apply(current, patch);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown execution step*implement_missing*");
    }

    [Fact]
    public void Apply_ReplacesAStepAndItsBinding()
    {
        var current = CreateDesign();
        var replacement = Step(
            "implement_b",
            "Implement billing with idempotency keys",
            ["implement_a"],
            ["contracts/billing@2"]);
        var patch = ValidPatch(current) with
        {
            DerivedFromArtifacts = ["contracts/billing@2"],
            ReplaceSteps = [replacement],
            ReplaceBindings = [Bind("implement_b", "implement", "2")],
        };

        var result = StagedExecutionGraphBuilder.Apply(current, patch);

        result.Graph.Steps.Should().Contain(replacement);
        result.Graph.Steps.Where(step => step.Id != "implement_b")
            .Should().Equal(current.Graph.Steps.Where(step => step.Id != "implement_b"));
        result.Bindings.Nodes.Single(node => node.StepId == "implement_b")
            .Binding.Descriptor.Version.Should().Be("2");
    }

    [Fact]
    public void Apply_RewiresAnEdgeWithoutReplacingTheStep()
    {
        var current = CreateDesign();
        var patch = ValidPatch(current) with
        {
            DependencyEdits =
            [
                new StepDependencyEdit
                {
                    StepId = "integrate",
                    DependsOn = ["implement_b"],
                },
            ],
        };

        var result = StagedExecutionGraphBuilder.Apply(current, patch);

        result.Graph.Steps.Single(step => step.Id == "integrate")
            .DependsOn.Should().Equal(["implement_b"]);
        result.Graph.Steps.Single(step => step.Id == "integrate")
            .Title.Should().Be("Integrate the implemented contexts");
    }

    [Fact]
    public void Identity_IsStableAndOrderInsensitive()
    {
        var current = CreateDesign();
        var reordered = new PlannedExecutionDesign(
            current.Graph with { Steps = current.Graph.Steps.Reverse().ToArray() },
            current.Bindings with { Nodes = current.Bindings.Nodes.Reverse().ToArray() });

        StagedExecutionDesignIdentity.Compute(current)
            .Should().Be(StagedExecutionDesignIdentity.Compute(reordered));
        StagedExecutionDesignIdentity.Compute(current)
            .Should().StartWith(StagedExecutionDesignIdentity.FingerprintScheme);
    }

    [Fact]
    public void Patch_ReportsEveryTouchedStepId()
    {
        var current = CreateDesign();
        var patch = ValidPatch(current) with
        {
            AddSteps = [Step("implement_cache", "Implement cache", ["implement_a"])],
            AddBindings = [Bind("implement_cache", "implement")],
            ReplaceBindings = [Bind("implement_b", "implement", "2")],
            RemoveStepIds = ["test"],
            DependencyEdits =
            [
                new StepDependencyEdit
                {
                    StepId = "integrate",
                    DependsOn = ["implement_a", "implement_b", "implement_cache"],
                },
            ],
        };

        patch.AffectedStepIds().Should().BeEquivalentTo(
            "implement_cache", "implement_b", "test", "integrate");
    }

    private static PlannedExecutionDesign CreateDesign()
    {
        var steps = new[]
        {
            Step("implement_a", "Implement todos", []),
            Step("implement_b", "Implement billing", ["implement_a"], ["contracts/billing@1"]),
            Step("integrate", "Integrate the implemented contexts", ["implement_a", "implement_b"]),
            Step("test", "Verify the integrated implementation", ["integrate"]),
        };
        var bindings = new[]
        {
            Bind("implement_a", "implement"),
            Bind("implement_b", "implement"),
            Bind("integrate", "integrate"),
            Bind("test", "verify"),
        };
        return new PlannedExecutionDesign(
            new PlannedExecutionGraph
            {
                WorkflowName = "implementation",
                InputType = "string",
                OutputType = "string",
                Steps = steps,
            },
            new PlannedExecutionBindings
            {
                WorkflowName = "implementation",
                Nodes = bindings,
            });
    }

    private static PlannedExecutionStep Step(
        string id,
        string title,
        string[] dependsOn,
        string[]? requiredArtifacts = null) => new()
        {
            Id = id,
            Title = title,
            DependsOn = dependsOn,
            RequiredArtifacts = requiredArtifacts ?? [],
            AcceptanceCriteria = [],
        };

    private static PlannedNodeBinding Bind(string stepId, string role, string version = "1") => new()
    {
        StepId = stepId,
        Binding = new PlannedExecutionBinding
        {
            Role = role,
            Capability = "code.modify",
            ModelProfile = "implementation",
            ContextArtifacts = ["solution-topology/main@1"],
            Descriptor = new DescriptorReference(
                DescriptorKind.Activity,
                "guyabano.execute",
                version,
                new ContentDigest("sha256", "test/v1", new string('a', 64))),
        },
    };

    private static WorkflowPatch ValidPatch(PlannedExecutionDesign current) => new()
    {
        BaseDesignFingerprint = StagedExecutionDesignIdentity.Compute(current),
        DerivedFromArtifacts = ["contracts/billing@1"],
        Rationale = "Add cache support discovered during implementation.",
        AddSteps = [],
        ReplaceSteps = [],
        RemoveStepIds = [],
        AddBindings = [],
        ReplaceBindings = [],
        DependencyEdits = [],
    };
}
