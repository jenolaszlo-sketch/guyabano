using FluentAssertions;
using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class ArchitectureReviewSemanticRepairTests
{
    [Fact]
    public void Repair_MakesUserInputFindingBlockingAndRejectsApproval()
    {
        var review = CreateReview(
            approved: true,
            ArchitectureReviewSeverity.Warning,
            requiresUserInput: true);

        var repaired = ArchitectureReviewSemanticRepair.Repair(
            review,
            out var attempts);

        repaired.Findings[0].Severity.Should().Be(
            ArchitectureReviewSeverity.Blocking);
        repaired.Approved.Should().BeFalse();
        attempts.Should().Contain(attempt =>
            attempt.Name == "semantic/required-user-input-severity" &&
            attempt.Status == LlmRepairStatus.Succeeded);
        attempts.Should().Contain(attempt =>
            attempt.Name == "semantic/approval-consistency" &&
            attempt.Status == LlmRepairStatus.Succeeded);
        ArchitectureReviewValidator.Validate(
                PlanTestData.Create(),
                repaired)
            .Should().BeEmpty();
    }

    [Fact]
    public void Repair_DerivesApprovalFromFindingSeverities()
    {
        var review = CreateReview(
            approved: false,
            ArchitectureReviewSeverity.Warning,
            requiresUserInput: false);

        var repaired = ArchitectureReviewSemanticRepair.Repair(
            review,
            out var attempts);

        repaired.Approved.Should().BeTrue();
        attempts.Should().Contain(attempt =>
            attempt.Name == "semantic/required-user-input-severity" &&
            attempt.Status == LlmRepairStatus.NotApplicable);
        attempts.Should().Contain(attempt =>
            attempt.Name == "semantic/approval-consistency" &&
            attempt.Status == LlmRepairStatus.Succeeded);
    }

    private static ArchitectureReview CreateReview(
        bool approved,
        ArchitectureReviewSeverity severity,
        bool requiresUserInput) => new()
        {
            Approved = approved,
            Findings =
        [
            new ArchitectureReviewFinding
            {
                Id = "F-003",
                Severity = severity,
                Category = "ProductAmbiguity",
                Summary = "Behavior needs clarification.",
                Evidence = ["TASK-001 does not define the behavior."],
                AffectedIds = ["TASK-001"],
                SuggestedResolution = "Ask the user.",
                RequiresUserInput = requiresUserInput
            }
        ]
        };
}
