using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class PatchPreservationValidatorTests
{
    [Fact]
    public void Validate_AcceptsACandidateThatOnlyTouchesAffectedSteps()
    {
        var prior = Plan();
        var candidate = Plan(extra: NewCacheNode());

        PatchPreservationValidator.Validate(prior, candidate, PatchAffecting("implement_cache"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Validate_RejectsAnUnmentionedNodeChange()
    {
        var prior = Plan();
        var candidate = Plan(extra: NewCacheNode(), billingVersion: "2");

        var drifts = PatchPreservationValidator.Validate(
            prior, candidate, PatchAffecting("implement_cache"));

        drifts.Should().ContainSingle().Which.Should().Contain("implement_b");
    }

    [Fact]
    public void Validate_RejectsAnUnmentionedNodeAddition()
    {
        var prior = Plan();
        var candidate = Plan(extra: NewCacheNode(), secondExtra: Node("implement_rogue", "implement_a"));

        var drifts = PatchPreservationValidator.Validate(
            prior, candidate, PatchAffecting("implement_cache"));

        drifts.Should().ContainSingle().Which.Should().Contain("implement_rogue");
    }

    [Fact]
    public void Validate_RejectsAnUnmentionedNodeRemoval()
    {
        var prior = Plan();
        var candidate = PlanWithoutBilling(NewCacheNode());

        var drifts = PatchPreservationValidator.Validate(
            prior, candidate, PatchAffecting("implement_cache"));

        drifts.Should().Contain(drift => drift.Contains("implement_b"));
    }

    [Fact]
    public void Validate_AllowsAReturnRepointedAtAnAffectedStep()
    {
        var prior = Plan();
        var candidate = Plan(extra: NewCacheNode(), returnFrom: "implement_cache");

        PatchPreservationValidator.Validate(prior, candidate, PatchAffecting("implement_cache"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Validate_RejectsAReturnRepointedOutsideThePatch()
    {
        var prior = Plan();
        var candidate = Plan(extra: NewCacheNode(), returnFrom: "implement_a");

        var drifts = PatchPreservationValidator.Validate(
            prior, candidate, PatchAffecting("implement_cache"));

        drifts.Should().ContainSingle().Which.Should().Contain("return");
    }

    [Fact]
    public void Validate_RejectsDroppingANodeThePatchDidNotRemove()
    {
        var prior = Plan();
        var candidate = PlanWithoutBilling(NewCacheNode());

        var drifts = PatchPreservationValidator.Validate(
            prior, candidate, PatchAffecting("implement_b", "implement_cache"));

        drifts.Should().ContainSingle().Which.Should().Contain("implement_b");
    }

    private static ContentDigest Digest(char c) => new("sha256", "test/v1", new string(c, 64));

    private static DescriptorReference Activity(string version = "1") =>
        new(DescriptorKind.Activity, "guyabano.execute", version, Digest('a'));

    private static string Path(string name) => StructuralNodeIdentity.Create("demo", name);

    private static ActivityNode Node(string name, string dependsOn, string version = "1")
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var arguments = dependsOn.Length == 0
            ? new List<ArgumentBinding>()
            : new List<ArgumentBinding>
            {
                new("answer", new NodeOutputBinding(Path(dependsOn), [])),
            };
        return new ActivityNode(name, Path(name), Activity(version), arguments, str);
    }

    private static ActivityNode NewCacheNode() => Node("implement_cache", "implement_a");

    private static WorkflowPlan Plan(
        ActivityNode? extra = null,
        ActivityNode? secondExtra = null,
        string billingVersion = "1",
        string returnFrom = "implement_b")
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var builder = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(Node("implement_a", string.Empty))
            .AddNode(Node("implement_b", "implement_a", billingVersion));
        if (extra is not null)
        {
            builder = builder.AddNode(extra);
        }

        if (secondExtra is not null)
        {
            builder = builder.AddNode(secondExtra);
        }

        return builder
            .AddNode(new ReturnNode(
                "return_result",
                Path("return_result"),
                new NodeOutputBinding(Path(returnFrom), [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([Path("implement_a")]),
                    new WorkflowExecutionPhase([Path("implement_b")]),
                    new WorkflowExecutionPhase([Path("return_result")]),
                ]),
            ]))
            .Build();
    }

    private static WorkflowPlan PlanWithoutBilling(ActivityNode extra)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(Node("implement_a", string.Empty))
            .AddNode(extra)
            .AddNode(new ReturnNode(
                "return_result",
                Path("return_result"),
                new NodeOutputBinding(Path("implement_cache"), [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([Path("implement_a")]),
                    new WorkflowExecutionPhase([Path("implement_cache")]),
                    new WorkflowExecutionPhase([Path("return_result")]),
                ]),
            ]))
            .Build();
    }

    private static WorkflowPatch PatchAffecting(params string[] stepIds) => new()
    {
        BaseDesignFingerprint = "test-base",
        DerivedFromArtifacts = ["contracts/billing@1"],
        Rationale = "Test patch.",
        AddSteps = stepIds.Select(id => new PlannedExecutionStep
        {
            Id = id,
            Title = "Test step",
            DependsOn = ["implement_a"],
            RequiredArtifacts = [],
            AcceptanceCriteria = [],
        }).ToArray(),
        ReplaceSteps = [],
        RemoveStepIds = [],
        AddBindings = stepIds.Select(id => new PlannedNodeBinding
        {
            StepId = id,
            Binding = new PlannedExecutionBinding
            {
                Role = "implement",
                Capability = "code.modify",
                ModelProfile = "implementation",
                ContextArtifacts = [],
                Descriptor = Activity(),
            },
        }).ToArray(),
        ReplaceBindings = [],
        DependencyEdits = [],
    };
}
