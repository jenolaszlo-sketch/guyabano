using FluentAssertions;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class RequirementTraceabilityValidatorTests
{
    [Fact]
    public void Validate_RejectsAcceptanceCriterionWithoutKnownUseCase()
    {
        var plan = PlanTestData.Create();
        plan.AcceptanceCriteria[0] = new PlanAcceptanceCriterion
        {
            Id = "AC-001",
            UseCaseId = "UC-UNKNOWN",
            BoundedContext = "TodoManagement",
            Feature = "Create todo",
            Scenario = "Create a valid todo",
            Given = ["The API is running"],
            When = ["A valid title is submitted"],
            Then = ["The API returns HTTP 201"],
            VerificationKinds = ["IntegrationTest"]
        };

        CodeGenerationPlanValidator.Validate(plan)
            .Should().Contain(error => error.Contains("UC-UNKNOWN"));
    }

    [Fact]
    public void Validate_RejectsCrossContextAcceptanceOwnership()
    {
        var plan = PlanTestData.Create();
        plan.AcceptanceCriteria[0] = new PlanAcceptanceCriterion
        {
            Id = "AC-001",
            UseCaseId = "UC-CREATE-TODO",
            BoundedContext = "OtherContext",
            Feature = "Create todo",
            Scenario = "Create a valid todo",
            Given = ["The API is running"],
            When = ["A valid title is submitted"],
            Then = ["The API returns HTTP 201"],
            VerificationKinds = ["IntegrationTest"]
        };

        CodeGenerationPlanValidator.Validate(plan)
            .Should().Contain(error => error.Contains(
                "does not share bounded-context ownership"));
    }
}
