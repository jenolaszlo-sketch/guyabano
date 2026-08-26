using FluentAssertions;
using Penghou.Baize.Tools.Schema;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class PlanningGapResolutionTests
{
    [Fact]
    public void ApplyDomainResolutions_ConvertsAmbiguitiesToDocumentedDefaults()
    {
        var domain = new DomainDiscovery
        {
            Mission = new ProductMission
            {
                GuidingIntent = "Manage todos.",
                SuccessOutcomes = ["Todos can be managed."],
                Constraints = [],
                NonGoals = []
            },
            Title = "Todo API",
            Summary = "Manages todos.",
            Terms = [],
            Capabilities = [],
            UseCases = [],
            QualityAttributes = [],
            Assumptions = [],
            InferredDefaults = [],
            ProductAmbiguities =
            [
                new DiscoveredProductAmbiguity
                {
                    Question = "Should duplicate titles be rejected?",
                    WhyItMatters = "Duplicate semantics were not specified.",
                    AffectedCapabilities = ["CreateTodo"]
                }
            ]
        };
        var resolution = new PlanningGapResolution
        {
            ResolutionKind = "PragmaticDomainDefault",
            Decision = "Allow duplicate titles because todo identity is ID-based.",
            Reasons = ["Separate todos may legitimately share a title."],
            AlternativesConsidered = ["Reject duplicate normalized titles"],
            Consequences = ["Clients distinguish todos by ID."],
            UserOverridable = true,
            RequiresUserInput = false,
            UserQuestion = string.Empty
        };

        var resolved = CodeGenerationPlanningService.ApplyDomainResolutions(
            domain,
            [resolution]);

        resolved.ProductAmbiguities.Should().BeEmpty();
        var inferred = resolved.InferredDefaults.Should()
            .ContainSingle().Subject;
        inferred.Subject.Should().Be("Should duplicate titles be rejected?");
        inferred.Decision.Should().Contain("Allow duplicate titles");
        inferred.AffectedCapabilities.Should().Contain("CreateTodo");
    }

    [Fact]
    public void GenerateSchema_RequiresResolutionDecisionAndEscalationFields()
    {
        var schema = JsonSchemaGenerator
            .GenerateSchemaNode<PlanningGapResolution>()
            .AsObject();
        var required = schema["required"]!
            .AsArray()
            .Select(item => item!.GetValue<string>());

        required.Should().Contain(
        [
            "resolutionKind",
            "decision",
            "reasons",
            "consequences",
            "requiresUserInput",
            "userQuestion"
        ]);
    }
}
