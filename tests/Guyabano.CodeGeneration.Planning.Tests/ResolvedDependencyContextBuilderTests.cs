using FluentAssertions;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class ResolvedDependencyContextBuilderTests
{
    [Fact]
    public void Build_CollectsTransitiveUpstreamArtifactsInPlanOrder()
    {
        var plan = CreateDependencyPlan();
        var builder = new ResolvedDependencyContextBuilder();

        var result = builder.Build(
            plan,
            "T-Api",
            [
                CreateDecomposition(
                    "T-Store",
                    "T-Store-L1",
                    "src/Todo.Api/Data/InMemoryTodoStore.cs",
                    "Todo.Api.Data",
                    "Todo.Api.Data.InMemoryTodoStore"),
                CreateDecomposition(
                    "T-Service",
                    "T-Service-L1",
                    "src/Todo.Api/Services/TodoService.cs",
                    "Todo.Api.Services",
                    "TodoService")
            ]);

        result.Artifacts.Select(item => item.ArchitectureTaskId)
            .Should().Equal("T-Store", "T-Service");
        result.Artifacts.SelectMany(item =>
                item.FullyQualifiedTypeNames)
            .Should().Equal(
                "Todo.Api.Data.InMemoryTodoStore",
                "Todo.Api.Services.TodoService");
        result.EffectiveContracts.Should().ContainSingle(contract =>
            contract.Id == "CONTRACT-TODO-SERVICE" &&
            contract.Members.Contains("Todo Create(string title)"));
    }

    [Fact]
    public void Build_RejectsMissingCodeGenerationDependencyArtifact()
    {
        var plan = CreateDependencyPlan();
        var builder = new ResolvedDependencyContextBuilder();

        var action = () => builder.Build(
            plan,
            "T-Api",
            [
                CreateDecomposition(
                    "T-Service",
                    "T-Service-L1",
                    "src/Todo.Api/Services/TodoService.cs",
                    "Todo.Api.Services",
                    "TodoService")
            ]);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*T-Store*no validated decomposition artifact*");
    }

    [Fact]
    public void Build_ExposesContractsDeclaredByTestedDependenciesEvenWhenLeavesDoNotRepeatThem()
    {
        var plan = CreateDependencyPlan();
        var service = plan.Tasks.Single(task => task.Id == "T-Service");
        service.ContractIds.Add("CONTRACT-CREATE-RESULT");
        plan.Contracts.Add(new PlannedContract
        {
            Id = "CONTRACT-CREATE-RESULT",
            Name = "CreateTodoResult",
            Kind = "Record",
            ModuleId = service.ModuleId!,
            Purpose = "Reports the result returned by the tested service.",
            Members = ["Todo Created"]
        });

        var result = new ResolvedDependencyContextBuilder().Build(
            plan,
            "T-Api",
            [
                CreateDecomposition(
                    "T-Store",
                    "T-Store-L1",
                    "src/Todo.Api/Data/InMemoryTodoStore.cs",
                    "Todo.Api.Data",
                    "Todo.Api.Data.InMemoryTodoStore"),
                CreateDecomposition(
                    "T-Service",
                    "T-Service-L1",
                    "src/Todo.Api/Services/TodoService.cs",
                    "Todo.Api.Services",
                    "TodoService")
            ]);

        result.EffectiveContracts.Should().Contain(contract =>
            contract.Id == "CONTRACT-CREATE-RESULT");
    }

    [Fact]
    public void Build_RejectsDuplicateFullyQualifiedTypeOwnership()
    {
        var plan = CreateDependencyPlan();
        var builder = new ResolvedDependencyContextBuilder();

        var action = () => builder.Build(
            plan,
            "T-Api",
            [
                CreateDecomposition(
                    "T-Store",
                    "T-Store-L1",
                    "src/Todo.Api/Data/First.cs",
                    "Todo.Api",
                    "Dependency"),
                CreateDecomposition(
                    "T-Service",
                    "T-Service-L1",
                    "src/Todo.Api/Services/Second.cs",
                    "Todo.Api",
                    "Dependency")
            ]);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Todo.Api.Dependency*both*");
    }

    private static CodeGenerationPlan CreateDependencyPlan()
    {
        var plan = PlanTestData.Create();
        plan.Tasks.Clear();
        plan.Tasks.Add(PlanTestData.CreateTask(
            "T-Store",
            2));
        plan.Tasks.Add(PlanTestData.CreateTask(
            "T-Service",
            2,
            dependsOn: ["T-Store"]));
        plan.Tasks.Add(PlanTestData.CreateTask(
            "T-Api",
            2,
            dependsOn: ["T-Service"]));
        return plan;
    }

    private static TaskDecompositionArtifactPayload CreateDecomposition(
        string parentTaskId,
        string leafTaskId,
        string path,
        string @namespace,
        string typeName) =>
        new(
            parentTaskId,
            new CodeGenerationTaskDecomposition
            {
                ParentTaskId = parentTaskId,
                Status = TaskDecompositionStatus.Ready,
                ArchitectureGaps = [],
                LeafTasks =
                [
                    new CodeGenerationLeafTask
                    {
                        Id = leafTaskId,
                        Title = "Resolve dependency",
                        Objective = "Create a dependency type.",
                        ComplexityPoints = 1,
                        DependsOn = [],
                        ContractIds = ["CONTRACT-TODO-SERVICE"],
                        AcceptanceCriterionIds = [],
                        DecisionIds = ["ADR-001"],
                        ImplementationRequirements = ["Create the type."],
                        VerificationKinds = ["Compilation"],
                        Artifacts =
                        [
                            new DecomposedArtifactPlan
                            {
                                Path = path,
                                Kind = "CSharpClass",
                                Namespace = @namespace,
                                TypeNames = [typeName],
                                Requirements = ["Declare the type."]
                            }
                        ]
                    }
                ]
            });
}
