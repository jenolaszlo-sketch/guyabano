using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowProgressTests;

public sealed class ArchitectureGapResolutionTests
{
    [Fact]
    public void ApplyResolutions_ReplacesSuggestionWithFocusedDecision()
    {
        var review = new ArchitectureReview
        {
            Approved = false,
            Findings =
            [
                new ArchitectureReviewFinding
                {
                    Id = "GAP-01",
                    Severity = ArchitectureReviewSeverity.Blocking,
                    Category = "DomainConstraint",
                    Summary = "Todo title length is unspecified.",
                    Evidence = ["The title contract has no maximum."],
                    AffectedIds = ["CONTRACT-TODO"],
                    SuggestedResolution = "Choose a maximum.",
                    RequiresUserInput = true
                }
            ]
        };
        var resolution = new ArchitectureGapResolution
        {
            FindingId = "GAP-01",
            ResolutionKind = "BestPracticeDefault",
            Decision = "Limit todo titles to 200 characters.",
            DecisionRecord = new ArchitectureDecision
            {
                Id = "ADR-RESOLUTION-GAP-01",
                Title = "Bound todo title length",
                Decision = "Limit todo titles to 200 characters.",
                Reasons = ["Keeps titles concise while supporting normal use."],
                AlternativesRejected = ["Unlimited title length"],
                RelatedPackages = []
            },
            AppliedPractice = new ArchitecturePractice
            {
                Id = "todo.title-length",
                Title = "Bound todo title length",
                Guidance = "Limit todo titles to 200 characters.",
                Applicability = "Todo title input in this project.",
                Reasons = ["Keeps titles concise."],
                Scope = "Project"
            },
            ReusedExistingPractice = false,
            Reasons = ["Keeps titles concise while supporting normal use."],
            AlternativesConsidered = ["Unlimited title length"],
            Consequences = ["Longer titles are rejected with validation details."],
            AffectedIds = ["CONTRACT-TODO"],
            UserOverridable = true,
            RequiresUserInput = false,
            UserQuestion = string.Empty
        };
        var result = new ArchitectureGapResolutionWorkflowResult(
            true,
            "None",
            null,
            "deepseek-v4-pro",
            resolution,
            false,
            [],
            null,
            null,
            null,
            null);

        var amendedReview = CodeGenerationWorkflow.ApplyResolutions(
            review,
            [result]);

        var finding = amendedReview.Findings.Should().ContainSingle().Subject;
        finding.RequiresUserInput.Should().BeFalse();
        finding.SuggestedResolution.Should().Contain("200 characters");
        finding.SuggestedResolution.Should().Contain("ADR-RESOLUTION-GAP-01");
        finding.SuggestedResolution.Should().Contain("todo.title-length");
        finding.SuggestedResolution.Should().Contain("Alternatives considered");
        finding.Evidence.Should().Contain(
            "Focused resolution kind: BestPracticeDefault");

        var workflowResult = new CodeGenerationWorkflowResult(
            true,
            "None",
            null,
            "deepseek-v4-pro",
            false,
            [],
            [],
            []) with
        {
            ArchitecturePractices = []
        };
        var merged = CodeGenerationWorkflow.MergeArchitectureResolutions(
            workflowResult,
            [result]);
        merged.ArchitecturePractices.Should().ContainSingle()
            .Which.Id.Should().Be("todo.title-length");
    }
}
