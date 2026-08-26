using Guyabano.CodeGeneration.Planning;

namespace Guyabano.CodeGeneration.Planning.Tests;

internal static class PlanTestData
{
    public static CodeGenerationPlan Create() =>
        new()
        {
            Mission = new ProductMission
            {
                GuidingIntent = "Provide a small in-memory todo API.",
                SuccessOutcomes = ["Todo behavior is available and verified."],
                Constraints = ["Use in-memory storage."],
                NonGoals = ["Durable persistence"]
            },
            Title = "Todo API",
            Summary = "Create a small in-memory todo API.",
            Assumptions = [],
            Solution = new PlannedSolution
            {
                Name = "Todo",
                Path = "Todo.sln"
            },
            Projects =
            [
                new PlannedProject
                {
                    Name = "Todo.Api",
                    Path = "src/Todo.Api/Todo.Api.csproj",
                    Kind = "WebApi",
                    Role = ProjectRole.CompositionRoot,
                    TargetFramework = "net10.0",
                    Responsibilities = ["Expose todo endpoints."],
                    ProjectDependencies = [],
                    Packages = []
                }
            ],
            Modules =
            [
                new PlannedModule
                {
                    Id = "MOD-TODOS",
                    Name = "Todos",
                    BoundedContext = "TodoManagement",
                    ProjectName = "Todo.Api",
                    Responsibilities = ["Manage todo behavior."]
                }
            ],
            Contracts =
            [
                new PlannedContract
                {
                    Id = "CONTRACT-TODO-SERVICE",
                    Name = "ITodoService",
                    Kind = "Interface",
                    ModuleId = "MOD-TODOS",
                    Purpose = "Define todo operations.",
                    Members = ["Todo Create(string title)"]
                }
            ],
            Decisions =
            [
                new ArchitectureDecision
                {
                    Id = "ADR-001",
                    Title = "Use in-memory storage",
                    Decision = "Use a concurrent in-memory collection.",
                    Reasons = ["The request excludes a database."],
                    AlternativesRejected = ["Relational database"],
                    RelatedPackages = []
                }
            ],
            ArchitectureNotes = [],
            UseCases =
            [
                new PlanUseCase
                {
                    Id = "UC-CREATE-TODO",
                    Name = "CreateTodo",
                    Capability = "Create todo",
                    BoundedContext = "TodoManagement",
                    Actor = "API consumer",
                    Objective = "Create a todo.",
                    Preconditions = [],
                    Inputs = ["title"],
                    BusinessRules = ["A title is required."],
                    Outcomes = ["A todo is created."],
                    ErrorOutcomes = ["Invalid input is rejected."],
                    AcceptanceCriterionIds = ["AC-001"]
                }
            ],
            AcceptanceCriteria =
            [
                new PlanAcceptanceCriterion
                {
                    Id = "AC-001",
                    UseCaseId = "UC-CREATE-TODO",
                    BoundedContext = "TodoManagement",
                    Feature = "Create todo",
                    Scenario = "Create a valid todo",
                    Given = ["The API is running"],
                    When = ["A valid title is submitted"],
                    Then = ["The API returns HTTP 201"],
                    VerificationKinds = ["IntegrationTest"]
                }
            ],
            Tasks = [CreateTask("TASK-001", 3)]
        };

    public static GenerationTaskPlan CreateTask(
        string id,
        int points,
        List<string>? dependsOn = null,
        List<string>? contractIds = null) =>
        new()
        {
            Id = id,
            Title = "Implement todo behavior",
            Objective = "Implement the todo service and endpoint.",
            ExecutionKind = PlanTaskExecutionKind.CodeGeneration,
            ModuleId = "MOD-TODOS",
            BoundedContext = "TodoManagement",
            ComplexityPoints = points,
            ComplexityReasons = ["Several cohesive files."],
            DecompositionRecommended = points >= 8,
            EstimatedFiles = 3,
            DependsOn = dependsOn ?? [],
            ContractIds = contractIds ?? ["CONTRACT-TODO-SERVICE"],
            Relationships = ComponentRelationshipPlan.Empty,
            DecisionIds = ["ADR-001"],
            AcceptanceCriterionIds = ["AC-001"],
            Deliverables = ["Todo service", "HTTP endpoint"],
            VerificationKinds = ["UnitTest", "IntegrationTest"]
        };

    public static GenerationTaskPlan CreateScaffoldingTask(
        string id = "TASK-SCAFFOLD") =>
        new()
        {
            Id = id,
            Title = "Create solution scaffolding",
            Objective = "Create the solution and declared projects.",
            ExecutionKind = PlanTaskExecutionKind.Scaffolding,
            ModuleId = null,
            BoundedContext = null,
            ComplexityPoints = 1,
            ComplexityReasons = ["Deterministic tooling."],
            DecompositionRecommended = false,
            EstimatedFiles = 3,
            DependsOn = [],
            ContractIds = [],
            Relationships = ComponentRelationshipPlan.Empty,
            DecisionIds = [],
            AcceptanceCriterionIds = [],
            Deliverables = ["Solution and project files"],
            VerificationKinds = ["Compilation"]
        };
}
