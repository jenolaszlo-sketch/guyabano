using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowProgressTests;

public sealed class ArchitectureReviewLoopPolicyTests
{
    [Fact]
    public void CanAcceptArchitecture_FirstPassWarning_RequiresDecisionIntegration()
    {
        var review = CreateReview(
            ArchitectureReviewSeverity.Warning,
            approved: true);

        CodeGenerationWorkflow.CanAcceptArchitecture(review, 1)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void CanAcceptArchitecture_FinalPassWarning_IsAccepted()
    {
        var review = CreateReview(
            ArchitectureReviewSeverity.Warning,
            approved: true);

        CodeGenerationWorkflow.CanAcceptArchitecture(
                review,
                CodeGenerationWorkflowConstants.MaximumArchitectureReviewPasses)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void CanAcceptArchitecture_FinalPassBlockingFinding_IsRejected()
    {
        var review = CreateReview(
            ArchitectureReviewSeverity.Blocking,
            approved: false);

        CodeGenerationWorkflow.CanAcceptArchitecture(
                review,
                CodeGenerationWorkflowConstants.MaximumArchitectureReviewPasses)
            .Should()
            .BeFalse();
    }

    private static ArchitectureReview CreateReview(
        ArchitectureReviewSeverity severity,
        bool approved) =>
        new()
        {
            Approved = approved,
            Findings =
            [
                new ArchitectureReviewFinding
                {
                    Id = "F-01",
                    Severity = severity,
                    Category = "Coverage",
                    Summary = "A task omits an acceptance criterion.",
                    Evidence = ["AC-01 is not assigned."],
                    AffectedIds = ["AC-01"],
                    SuggestedResolution = "Assign AC-01.",
                    RequiresUserInput = false
                }
            ]
        };
}
