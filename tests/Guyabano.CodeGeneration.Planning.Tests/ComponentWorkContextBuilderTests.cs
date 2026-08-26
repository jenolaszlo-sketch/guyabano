using FluentAssertions;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class ComponentWorkContextBuilderTests
{
    [Fact]
    public void Build_ProjectsOnlyRelevantArchitectureAndTypedDependencies()
    {
        var plan = PlanTestData.Create();
        var parent = plan.Tasks.Single();
        parent.DependsOn.Add("TASK-STORE");
        parent.Relationships.UsesConcreteTaskIds.Add("TASK-STORE");
        plan.Tasks.Add(new GenerationTaskPlan
        {
            Id = "TASK-STORE",
            Title = "Implement todo storage",
            Objective = "Store todos.",
            ExecutionKind = PlanTaskExecutionKind.CodeGeneration,
            ModuleId = "MOD-TODOS",
            BoundedContext = "TodoManagement",
            ComplexityPoints = 2,
            ComplexityReasons = ["One focused component."],
            DecompositionRecommended = false,
            EstimatedFiles = 1,
            DependsOn = [],
            ContractIds = [],
            Relationships = ComponentRelationshipPlan.Empty,
            DecisionIds = [],
            AcceptanceCriterionIds = [],
            Deliverables = ["src/Todo.Api/InMemoryTodoStore.cs"],
            VerificationKinds = ["UnitTest"]
        });
        plan.Contracts.Add(new PlannedContract
        {
            Id = "CONTRACT-UNRELATED",
            Name = "IUnrelated",
            Kind = "Interface",
            ModuleId = "MOD-TODOS",
            Purpose = "Not required by the target.",
            Members = ["void Ignore()"]
        });
        plan.ArchitectureNotes.Add(new ArchitectureNote
        {
            Id = "NOTE-UNRELATED",
            Category = ArchitectureNoteCategory.InferredDefault,
            Subject = "Unrelated concern",
            MissingInformation = "None",
            Decision = "Ignore it.",
            Reasons = ["It is unrelated."],
            Impact = "None",
            AffectedIds = ["CONTRACT-UNRELATED"],
            UserOverridable = true
        });

        var context = new ComponentWorkContextBuilder().Build(
            plan,
            parent.Id,
            ResolvedDependencyContext.Empty);

        context.Project.Role.Should().Be(ProjectRole.CompositionRoot);
        context.Contracts.Select(item => item.Id)
            .Should().Equal("CONTRACT-TODO-SERVICE");
        context.ArchitectureNotes.Should().BeEmpty();
        context.ComponentDependencies.Should().ContainSingle().Which
            .Should().BeEquivalentTo(new ComponentWorkDependency(
                "TASK-STORE",
                "Implement todo storage",
                "Todo.Api",
                ProjectRole.CompositionRoot,
                ComponentDependencyKind.UsesConcreteComponent,
                ["src/Todo.Api/InMemoryTodoStore.cs"]));
    }
}
