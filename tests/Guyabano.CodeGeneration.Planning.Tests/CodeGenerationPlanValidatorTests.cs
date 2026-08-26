using FluentAssertions;
using Guyabano.CodeGeneration.Planning;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class CodeGenerationPlanValidatorTests
{
    [Fact]
    public void Validate_AcceptsConsistentDependencyGraph()
    {
        var errors = CodeGenerationPlanValidator.Validate(
            PlanTestData.Create());

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_RejectsUnsupportedPointsAndUnknownReferences()
    {
        var plan = PlanTestData.Create();
        plan.Tasks[0] = PlanTestData.CreateTask(
            id: "TASK-001",
            points: 4,
            dependsOn: ["TASK-404"],
            contractIds: ["CONTRACT-404"]);

        var errors = CodeGenerationPlanValidator.Validate(plan);

        errors.Should().Contain(error =>
            error.Contains("unsupported complexity points"));
        errors.Should().Contain(error =>
            error.Contains("unknown dependency 'TASK-404'"));
        errors.Should().Contain(error =>
            error.Contains("unknown contract 'CONTRACT-404'"));
    }

    [Fact]
    public void Validate_RejectsTaskDependencyCycle()
    {
        var plan = PlanTestData.Create();
        plan.Tasks.Add(PlanTestData.CreateTask(
            id: "TASK-002",
            points: 2,
            dependsOn: ["TASK-001"]));
        plan.Tasks[0] = PlanTestData.CreateTask(
            id: "TASK-001",
            points: 3,
            dependsOn: ["TASK-002"]);

        var errors = CodeGenerationPlanValidator.Validate(plan);

        errors.Should().Contain(
            "The task dependency graph contains a cycle.");
    }

    [Fact]
    public void Validate_AcceptsTraceableInferredDomainConstraint()
    {
        var plan = PlanTestData.Create();
        plan.ArchitectureNotes.Add(new ArchitectureNote
        {
            Id = "NOTE-TITLE-LENGTH",
            Category = ArchitectureNoteCategory.InferredDomainConstraint,
            Subject = "Todo title length",
            MissingInformation = "The request did not specify a maximum title length.",
            Decision = "Todo titles have a maximum length of 200 characters.",
            Reasons = ["A title should remain concise and bounded."],
            Impact = "Titles longer than 200 characters are rejected as invalid input.",
            AffectedIds = ["CONTRACT-TODO-SERVICE", "AC-001"],
            UserOverridable = true
        });

        CodeGenerationPlanValidator.Validate(plan).Should().BeEmpty();
    }

    [Fact]
    public void Validate_RejectsArchitectureNoteWithUnknownAffectedId()
    {
        var plan = PlanTestData.Create();
        plan.ArchitectureNotes.Add(new ArchitectureNote
        {
            Id = "NOTE-TITLE-LENGTH",
            Category = ArchitectureNoteCategory.InferredDomainConstraint,
            Subject = "Todo title length",
            MissingInformation = "The maximum was unspecified.",
            Decision = "Use 200 characters.",
            Reasons = ["Keep titles concise."],
            Impact = "Input validation is bounded.",
            AffectedIds = ["CONTRACT-404"],
            UserOverridable = true
        });

        CodeGenerationPlanValidator.Validate(plan).Should().Contain(
            "Architecture note 'NOTE-TITLE-LENGTH' references unknown architecture ID 'CONTRACT-404'.");
    }

    [Fact]
    public void Validate_AcceptsSolutionLevelScaffoldingTask()
    {
        var plan = PlanTestData.Create();
        plan.Tasks.Insert(0, PlanTestData.CreateScaffoldingTask());
        plan.Tasks[1] = PlanTestData.CreateTask(
            id: "TASK-001",
            points: 3,
            dependsOn: ["TASK-SCAFFOLD"]);

        var errors = CodeGenerationPlanValidator.Validate(plan);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_RejectsScaffoldingAssignedToModule()
    {
        var plan = PlanTestData.Create();
        plan.Tasks[0] = new GenerationTaskPlan
        {
            Id = "TASK-SCAFFOLD",
            Title = "Create solution scaffolding",
            Objective = "Create the solution and projects.",
            ExecutionKind = PlanTaskExecutionKind.Scaffolding,
            ModuleId = "MOD-TODOS",
            ComplexityPoints = 1,
            ComplexityReasons = ["Deterministic tooling."],
            DecompositionRecommended = false,
            EstimatedFiles = 3,
            DependsOn = [],
            ContractIds = [],
            Relationships = ComponentRelationshipPlan.Empty,
            DecisionIds = [],
            AcceptanceCriterionIds = ["AC-001"],
            Deliverables = ["Solution and projects"],
            VerificationKinds = ["Compilation"]
        };

        var errors = CodeGenerationPlanValidator.Validate(plan);

        errors.Should().Contain(
            "Scaffolding task 'TASK-SCAFFOLD' must not reference a module.");
    }

    [Fact]
    public void Validate_RejectsCodeGenerationTaskThatBypassesScaffolding()
    {
        var plan = PlanTestData.Create();
        plan.Tasks.Insert(0, PlanTestData.CreateScaffoldingTask());

        var errors = CodeGenerationPlanValidator.Validate(plan);

        errors.Should().Contain(
            "Code-generation task 'TASK-001' does not depend on scaffolding task 'TASK-SCAFFOLD'.");
    }

    [Fact]
    public void Validate_RejectsTaskWhoseProjectCannotReachContractProject()
    {
        var plan = PlanTestData.Create();
        plan.Projects.Add(new PlannedProject
        {
            Name = "Todo.Contracts",
            Path = "src/Todo.Contracts/Todo.Contracts.csproj",
            Kind = "Contracts",
            Role = ProjectRole.Contracts,
            TargetFramework = "net10.0",
            Responsibilities = ["Define public contracts."],
            ProjectDependencies = [],
            Packages = []
        });
        plan.Modules.Add(new PlannedModule
        {
            Id = "MOD-CONTRACTS",
            Name = "TodoContracts",
            BoundedContext = "TodoManagement",
            ProjectName = "Todo.Contracts",
            Responsibilities = ["Own public contracts."]
        });
        plan.Contracts[0] = MoveContract(
            plan.Contracts[0],
            "MOD-CONTRACTS");

        var errors = CodeGenerationPlanValidator.Validate(plan);

        errors.Should().Contain(
            "Task 'TASK-001' in project 'Todo.Api' consumes contract 'CONTRACT-TODO-SERVICE' from project 'Todo.Contracts', but no project dependency path exists.");
    }

    [Fact]
    public void Validate_AcceptsTransitivePathToContractProject()
    {
        var plan = PlanTestData.Create();
        plan.Projects[0].ProjectDependencies.Add("Todo.Core");
        plan.Projects.Add(new PlannedProject
        {
            Name = "Todo.Core",
            Path = "src/Todo.Core/Todo.Core.csproj",
            Kind = "Library",
            Role = ProjectRole.Application,
            TargetFramework = "net10.0",
            Responsibilities = ["Implement todo behavior."],
            ProjectDependencies = ["Todo.Contracts"],
            Packages = []
        });
        plan.Projects.Add(new PlannedProject
        {
            Name = "Todo.Contracts",
            Path = "src/Todo.Contracts/Todo.Contracts.csproj",
            Kind = "Contracts",
            Role = ProjectRole.Contracts,
            TargetFramework = "net10.0",
            Responsibilities = ["Define public contracts."],
            ProjectDependencies = [],
            Packages = []
        });
        plan.Modules.Add(new PlannedModule
        {
            Id = "MOD-CONTRACTS",
            Name = "TodoContracts",
            BoundedContext = "TodoManagement",
            ProjectName = "Todo.Contracts",
            Responsibilities = ["Own public contracts."]
        });
        plan.Contracts[0] = MoveContract(
            plan.Contracts[0],
            "MOD-CONTRACTS");

        CodeGenerationPlanValidator.Validate(plan).Should().BeEmpty();
    }

    [Fact]
    public void Validate_RejectsOutwardDomainProjectDependency()
    {
        var plan = PlanTestData.Create();
        plan.Projects.Add(new PlannedProject
        {
            Name = "Todo.Storage",
            Path = "src/Todo.Storage/Todo.Storage.csproj",
            Kind = "Library",
            Role = ProjectRole.Adapter,
            TargetFramework = "net10.0",
            Responsibilities = ["Persist todos."],
            ProjectDependencies = [],
            Packages = []
        });
        plan.Projects.Add(new PlannedProject
        {
            Name = "Todo.Contracts",
            Path = "src/Todo.Contracts/Todo.Contracts.csproj",
            Kind = "Library",
            Role = ProjectRole.Contracts,
            TargetFramework = "net10.0",
            Responsibilities = ["Define contracts."],
            ProjectDependencies = ["Todo.Storage"],
            Packages = []
        });

        CodeGenerationPlanValidator.Validate(plan).Should().Contain(
            "Project 'Todo.Contracts' with role 'Contracts' must not depend on project 'Todo.Storage' with role 'Adapter'.");
    }

    private static PlannedContract MoveContract(
        PlannedContract source,
        string moduleId) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            Kind = source.Kind,
            ModuleId = moduleId,
            Purpose = source.Purpose,
            Members = source.Members
        };
}
