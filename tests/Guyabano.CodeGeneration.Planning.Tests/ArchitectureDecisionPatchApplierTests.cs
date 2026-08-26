using FluentAssertions;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class ArchitectureDecisionPatchApplierTests
{
    [Fact]
    public void Apply_ReplacesOnlyAddressedEntitiesAndPreservesValidPlan()
    {
        var plan = PlanTestData.Create();
        var review = ArchitectureReviewValidatorTests.CreateReview(false);
        review.Findings[0].AffectedIds.Add("MOD-TODOS");
        var patch = CreatePatch();

        var integrated = ArchitectureDecisionPatchApplier.Apply(
            plan,
            review,
            [CreateResolution()],
            patch);

        integrated.Modules.Single().Responsibilities.Should()
            .ContainSingle("Validation belongs to the service per ADR-001.");
        integrated.Tasks.Single().DecisionIds.Should().Contain("ADR-001");
        integrated.Contracts.Should().Equal(plan.Contracts);
        integrated.ArchitectureNotes.Should().ContainSingle()
            .Which.Decision.Should().Contain("200");
        CodeGenerationPlanValidator.Validate(integrated).Should().BeEmpty();
    }

    [Fact]
    public void Apply_RejectsPatchThatDoesNotApplyEveryResolution()
    {
        var patch = CreatePatch();
        patch.AppliedResolutionIds.Clear();

        var action = () => ArchitectureDecisionPatchApplier.Apply(
            PlanTestData.Create(),
            ArchitectureReviewValidatorTests.CreateReview(false),
            [CreateResolution()],
            patch);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*every resolved finding exactly once*");
    }

    [Fact]
    public void Apply_RejectsReplacementOutsideResolvedFindingScope()
    {
        var action = () => ArchitectureDecisionPatchApplier.Apply(
            PlanTestData.Create(),
            ArchitectureReviewValidatorTests.CreateReview(false),
            [CreateResolution()],
            CreatePatch());

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside the resolved findings*MOD-TODOS*");
    }

    [Fact]
    public void Apply_RejectsChangedAuthoritativeDecisionRecord()
    {
        var plan = PlanTestData.Create();
        var review = ArchitectureReviewValidatorTests.CreateReview(false);
        review.Findings[0].AffectedIds.Add("MOD-TODOS");
        var patch = CreatePatch();
        var decision = patch.DecisionAdditions[0];
        patch.DecisionAdditions[0] = new ArchitectureDecision
        {
            Id = decision.Id,
            Title = decision.Title,
            Decision = "Use a different limit.",
            Reasons = decision.Reasons,
            AlternativesRejected = decision.AlternativesRejected,
            RelatedPackages = decision.RelatedPackages
        };

        var action = () => ArchitectureDecisionPatchApplier.Apply(
            plan,
            review,
            [CreateResolution()],
            patch);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*must apply authoritative ADR*unchanged*");
    }

    private static ArchitectureDecisionPatch CreatePatch() =>
        new()
        {
            AppliedResolutionIds = ["AR-01"],
            AssumptionsToAdd = [],
            ProjectReplacements = [],
            ModuleReplacements =
            [
                new PlannedModule
                {
                    Id = "MOD-TODOS",
                    Name = "Todos",
                    BoundedContext = "TodoManagement",
                    ProjectName = "Todo.Api",
                    Responsibilities =
                        ["Validation belongs to the service per ADR-001."]
                }
            ],
            ContractReplacements = [],
            ContractAdditions = [],
            DecisionReplacements = [],
            DecisionAdditions = [CreateResolution().DecisionRecord],
            ArchitectureNoteReplacements = [],
            ArchitectureNoteAdditions =
            [
                new ArchitectureNote
                {
                    Id = "NOTE-TITLE-LENGTH",
                    Category = ArchitectureNoteCategory.InferredDomainConstraint,
                    Subject = "Todo title length",
                    MissingInformation = "The maximum title length was unspecified.",
                    Decision = "Limit titles to 200 characters.",
                    Reasons = ["Titles should remain concise and bounded."],
                    Impact = "Longer titles are rejected.",
                    AffectedIds = ["CONTRACT-TODO-SERVICE", "AC-001"],
                    UserOverridable = true
                }
            ],
            AcceptanceCriterionReplacements = [],
            AcceptanceCriterionAdditions = [],
            TaskReplacements = [PlanTestData.CreateTask("TASK-001", 3)]
        };

    private static ArchitectureGapResolution CreateResolution() =>
        new()
        {
            FindingId = "AR-01",
            ResolutionKind = "ProjectDefault",
            Decision = "Limit todo titles to 200 characters.",
            DecisionRecord = new ArchitectureDecision
            {
                Id = "ADR-RESOLUTION-AR-01",
                Title = "Bound todo title length",
                Decision = "Limit todo titles to 200 characters.",
                Reasons = ["Titles remain useful and operationally bounded."],
                AlternativesRejected = ["Unlimited title length"],
                RelatedPackages = []
            },
            AppliedPractice = new ArchitecturePractice
            {
                Id = "todo.title-length",
                Title = "Bound todo title length",
                Guidance = "Limit todo titles to 200 characters.",
                Applicability = "Todo title input in this project.",
                Reasons = ["Titles should remain concise."],
                Scope = "Project"
            },
            ReusedExistingPractice = false,
            Reasons = ["Titles should remain concise."],
            AlternativesConsidered = ["Unlimited title length"],
            Consequences = ["Longer titles are rejected."],
            AffectedIds = ["TASK-001", "ADR-001", "MOD-TODOS"],
            UserOverridable = true,
            RequiresUserInput = false,
            UserQuestion = string.Empty
        };
}
