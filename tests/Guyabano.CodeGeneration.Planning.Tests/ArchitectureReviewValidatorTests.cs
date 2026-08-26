using FluentAssertions;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class ArchitectureReviewValidatorTests
{
    [Fact]
    public void Validate_AcceptsEvidenceBackedBlockingFinding()
    {
        var review = CreateReview(approved: false);

        ArchitectureReviewValidator.Validate(
                PlanTestData.Create(),
                review)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Validate_RejectsApprovalWithBlockingFindingAndUnknownId()
    {
        var review = CreateReview(approved: true);
        review.Findings[0].AffectedIds[0] = "UNKNOWN";

        var errors = ArchitectureReviewValidator.Validate(
            PlanTestData.Create(),
            review);

        errors.Should().Contain(error => error.Contains("UNKNOWN"));
        errors.Should().Contain(error => error.Contains("approval"));
    }

    [Fact]
    public void Validate_RejectsUserEscalationForOrdinaryArchitectureOmission()
    {
        var review = new ArchitectureReview
        {
            Approved = false,
            Findings =
            [
                new ArchitectureReviewFinding
                {
                    Id = "AR-01",
                    Severity = ArchitectureReviewSeverity.Blocking,
                    Category = "ArchitectureOmission",
                    Summary = "A technical default is missing.",
                    Evidence = ["CONTRACT-TODO-SERVICE is unbounded."],
                    AffectedIds = ["CONTRACT-TODO-SERVICE"],
                    SuggestedResolution = "Select and record a sensible default.",
                    RequiresUserInput = true
                }
            ]
        };

        ArchitectureReviewValidator.Validate(
                PlanTestData.Create(),
                review)
            .Should()
            .Contain(error => error.Contains("ProductAmbiguity"));
    }

    internal static ArchitectureReview CreateReview(bool approved) =>
        new()
        {
            Approved = approved,
            Findings =
            [
                new ArchitectureReviewFinding
                {
                    Id = "AR-01",
                    Severity = ArchitectureReviewSeverity.Blocking,
                    Category = "Contradiction",
                    Summary = "Task ownership contradicts its ADR.",
                    Evidence =
                    [
                        "TASK-001 assigns validation to the DTO.",
                        "ADR-001 assigns validation to the service."
                    ],
                    AffectedIds = ["TASK-001", "ADR-001"],
                    SuggestedResolution = "Align the task with ADR-001.",
                    RequiresUserInput = false
                }
            ]
        };
}
