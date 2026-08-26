using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowProgressTests;

public sealed class DecompositionArchitectureFeedbackTests
{
    [Fact]
    public void CreateReview_PreservesGapEvidenceAndScope()
    {
        var result = new CodeGenerationDecompositionWorkflowResult(
            "integration-tests",
            false,
            "ArchitectureGap",
            "missing entry point",
            "model",
            new CodeGenerationTaskDecomposition
            {
                ParentTaskId = "integration-tests",
                Status = TaskDecompositionStatus.ArchitectureGap,
                LeafTasks = [],
                ArchitectureGaps =
                [
                    new TaskArchitectureGap
                    {
                        Question = "Expose a public test entry point.",
                        Reason = "WebApplicationFactory requires a public Program type.",
                        AffectedContractIds = ["host-program"],
                        AffectedDecisionIds = ["d-webapp-factory"]
                    }
                ]
            },
            false,
            []);

        var review = CodeGenerationWorkflow.CreateArchitectureGapReview(result);

        review.Approved.Should().BeFalse();
        review.Findings.Should().ContainSingle();
        review.Findings[0].Severity.Should().Be(
            ArchitectureReviewSeverity.Blocking);
        review.Findings[0].AffectedIds.Should().BeEquivalentTo(
            "host-program",
            "d-webapp-factory");
        review.Findings[0].Evidence.Should().Contain(item =>
            item.Contains("WebApplicationFactory", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateReview_RejectsGapWithoutDetails()
    {
        var result = new CodeGenerationDecompositionWorkflowResult(
            "task",
            false,
            "ArchitectureGap",
            null,
            "model",
            new CodeGenerationTaskDecomposition
            {
                ParentTaskId = "task",
                Status = TaskDecompositionStatus.ArchitectureGap,
                LeafTasks = [],
                ArchitectureGaps = []
            },
            false,
            []);

        var action = () =>
            CodeGenerationWorkflow.CreateArchitectureGapReview(result);

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AffectedTask_MatchesChangedContractOrDecision()
    {
        var plan = CreatePlan(
            CreateTask("host", ["host-program"], []),
            CreateTask("tests", [], ["d-webapp-factory"]),
            CreateTask("unrelated", ["other-contract"], ["other-decision"]));
        IReadOnlySet<string> affected = new HashSet<string>(
            ["host-program", "d-webapp-factory"],
            StringComparer.Ordinal);

        CodeGenerationWorkflow.IsAffectedByArchitectureIntegration(
            plan, "host", affected).Should().BeTrue();
        CodeGenerationWorkflow.IsAffectedByArchitectureIntegration(
            plan, "tests", affected).Should().BeTrue();
        CodeGenerationWorkflow.IsAffectedByArchitectureIntegration(
            plan, "unrelated", affected).Should().BeFalse();
    }

    [Fact]
    public void MissingTask_IsTreatedAsAffected()
    {
        var plan = CreatePlan(CreateTask("remaining", [], []));

        CodeGenerationWorkflow.IsAffectedByArchitectureIntegration(
                plan,
                "removed-task",
                new HashSet<string>(StringComparer.Ordinal))
            .Should().BeTrue();
    }

    private static CodeGenerationPlan CreatePlan(
        params GenerationTaskPlan[] tasks) => new()
        {
            Title = "plan",
            Summary = "summary",
            Assumptions = [],
            Solution = null!,
            Projects = [],
            Modules = [],
            Contracts = [],
            Decisions = [],
            ArchitectureNotes = [],
            AcceptanceCriteria = [],
            Tasks = [.. tasks]
        };

    private static GenerationTaskPlan CreateTask(
        string id,
        List<string> contracts,
        List<string> decisions) => new()
        {
            Id = id,
            Title = id,
            Objective = id,
            ExecutionKind = PlanTaskExecutionKind.CodeGeneration,
            ModuleId = "module",
            ComplexityPoints = 1,
            ComplexityReasons = [],
            DecompositionRecommended = false,
            EstimatedFiles = 1,
            DependsOn = [],
            ContractIds = contracts,
            Relationships = ComponentRelationshipPlan.Empty,
            DecisionIds = decisions,
            AcceptanceCriterionIds = [],
            Deliverables = [],
            VerificationKinds = []
        };
}
