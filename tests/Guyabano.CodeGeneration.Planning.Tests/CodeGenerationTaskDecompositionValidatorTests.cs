using FluentAssertions;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class CodeGenerationTaskDecompositionValidatorTests
{
    [Fact]
    public void Validate_AcceptsExecutionReadyLeavesUsingExistingArchitecture()
    {
        var plan = PlanTestData.Create();
        var parent = plan.Tasks.Single();

        CodeGenerationTaskDecompositionValidator.Validate(
                plan,
                parent,
                CreateReady(parent.Id))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Validate_RejectsArchitecturalDriftAndUnsafeScope()
    {
        var plan = PlanTestData.Create();
        var parent = plan.Tasks.Single();
        var decomposition = CreateReady(parent.Id);
        decomposition.LeafTasks[0] = new CodeGenerationLeafTask
        {
            Id = "TASK-001-L01",
            Title = "Drifted task",
            Objective = "Drift architecture.",
            ComplexityPoints = 5,
            DependsOn = [],
            ContractIds = ["NEW-CONTRACT"],
            AcceptanceCriterionIds = ["AC-001"],
            DecisionIds = ["NEW-ADR"],
            ImplementationRequirements = ["Do work."],
            Artifacts =
            [
                new DecomposedArtifactPlan
                {
                    Path = "../Outside.cs",
                    Kind = "CSharpClass",
                    Namespace = "Outside",
                    TypeNames = ["Outside"],
                    Requirements = ["Escape scope."]
                }
            ],
            VerificationKinds = ["Compilation"]
        };

        var errors = CodeGenerationTaskDecompositionValidator.Validate(
            plan,
            parent,
            decomposition);

        errors.Should().Contain(error => error.Contains("1 or 2"));
        errors.Should().Contain(error => error.Contains("NEW-CONTRACT"));
        errors.Should().Contain(error => error.Contains("NEW-ADR"));
        errors.Should().Contain(error => error.Contains("outside project"));
    }

    [Fact]
    public void Validate_AcceptsExplicitArchitectureGapWithoutLeaves()
    {
        var plan = PlanTestData.Create();
        var parent = plan.Tasks.Single();
        var decomposition = new CodeGenerationTaskDecomposition
        {
            ParentTaskId = parent.Id,
            Status = TaskDecompositionStatus.ArchitectureGap,
            LeafTasks = [],
            ArchitectureGaps =
            [
                new TaskArchitectureGap
                {
                    Question = "How are validation errors represented?",
                    Reason = "The public error contract is unspecified.",
                    AffectedContractIds = ["CONTRACT-TODO-SERVICE"],
                    AffectedDecisionIds = []
                }
            ]
        };

        CodeGenerationTaskDecompositionValidator.Validate(
                plan,
                parent,
                decomposition)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Validate_AcceptsContractsResolvedFromUpstreamArtifacts()
    {
        var plan = PlanTestData.Create();
        var parent = plan.Tasks.Single();
        var decomposition = CreateReady(parent.Id);
        decomposition.LeafTasks[0].ContractIds.Add("CONTRACT-UPSTREAM");
        var dependencies = new ResolvedDependencyContext(
            [],
            [
                new ResolvedContractDependency(
                    "CONTRACT-UPSTREAM",
                    "ITodoStore",
                    "Interface",
                    "Stores todos.",
                    ["TodoItem? GetById(int id)"])
            ]);

        CodeGenerationTaskDecompositionValidator.Validate(
                plan,
                parent,
                decomposition,
                dependencies)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Validate_RejectsContractsAbsentFromParentAndUpstreamArtifacts()
    {
        var plan = PlanTestData.Create();
        var parent = plan.Tasks.Single();
        var decomposition = CreateReady(parent.Id);
        decomposition.LeafTasks[0].ContractIds.Add("CONTRACT-UNKNOWN");

        CodeGenerationTaskDecompositionValidator.Validate(
                plan,
                parent,
                decomposition,
                ResolvedDependencyContext.Empty)
            .Should()
            .Contain(error => error.Contains(
                "unknown contract 'CONTRACT-UNKNOWN'"));
    }

    [Fact]
    public void Validate_UnknownSiblingDependencyListsValidIdentifiers()
    {
        var plan = PlanTestData.Create();
        var parent = plan.Tasks.Single();
        var decomposition = CreateReady(parent.Id);
        decomposition.LeafTasks[0].DependsOn.Add(
            "TASK-TASK-001-L02");

        var errors = CodeGenerationTaskDecompositionValidator.Validate(
            plan,
            parent,
            decomposition);

        errors.Should().Contain(error =>
            error.Contains(
                "unknown sibling dependency 'TASK-TASK-001-L02'",
                StringComparison.Ordinal) &&
            error.Contains(
                "Valid sibling dependencies are: 'TASK-001-L01'",
                StringComparison.Ordinal));
    }

    internal static CodeGenerationTaskDecomposition CreateReady(
        string parentTaskId) =>
        new()
        {
            ParentTaskId = parentTaskId,
            Status = TaskDecompositionStatus.Ready,
            ArchitectureGaps = [],
            LeafTasks = [CreateLeaf()]
        };

    private static CodeGenerationLeafTask CreateLeaf() =>
        new()
        {
            Id = "TASK-001-L01",
            Title = "Implement todo service",
            Objective = "Implement the declared service contract.",
            ComplexityPoints = 2,
            DependsOn = [],
            ContractIds = ["CONTRACT-TODO-SERVICE"],
            AcceptanceCriterionIds = ["AC-001"],
            DecisionIds = ["ADR-001"],
            ImplementationRequirements =
                ["Implement every declared contract member."],
            Artifacts =
            [
                new DecomposedArtifactPlan
                {
                    Path = "src/Todo.Api/TodoService.cs",
                    Kind = "CSharpClass",
                    Namespace = "Todo.Api",
                    TypeNames = ["TodoService"],
                    Requirements = ["Implement ITodoService."]
                }
            ],
            VerificationKinds = ["Compilation", "UnitTest"]
        };
}
