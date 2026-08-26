using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationBuildRepairPlannerTests
{
    [Fact]
    public void Create_TargetsOwnedFailingArtifactAndEscalatesModelTier()
    {
        var plan = CreatePlan();
        var leaf = CreateLeaf();
        var decomposition = CreateDecomposition(leaf);
        var generation = CreateTaskResult(
            model: "small",
            modelTier: 1,
            files: ["src/Todo.Api/TodoService.cs"]);
        var build = new CodeGenerationBuildResult(
            false,
            1,
            "Build failed with one compiler error.",
            [
                new CodeGenerationBuildDiagnostic(
                    "dotnet",
                    "CS0103",
                    "Error",
                    "The name 'store' does not exist.",
                    "/workspace/generated/src/Todo.Api/TodoService.cs",
                    "src/Todo.Api/Todo.Api.csproj",
                    12,
                    9)
            ]);

        var requests = CodeGenerationBuildRepairPlanner.Create(
            plan,
            build,
            [generation],
            [decomposition],
            repairCycle: 1);

        var request = requests.Should().ContainSingle().Subject;
        request.IsBuildRepair.Should().BeTrue();
        request.StartingModelTier.Should().Be(2);
        request.Task.Artifacts.Should().ContainSingle()
            .Which.Path.Should().Be("src/Todo.Api/TodoService.cs");
        request.Correction.Should().NotBeNull();
        request.Correction!.Diagnostics.Should().ContainSingle(item =>
            item.Contains("CS0103") && item.Contains("(12,9)"));
    }

    [Fact]
    public void Create_TargetsPlannedProjectFileFailureAtHighestModelTier()
    {
        var build = new CodeGenerationBuildResult(
            false,
            1,
            "Project file is invalid.",
            [
                new CodeGenerationBuildDiagnostic(
                    "dotnet",
                    "MSB4025",
                    "Error",
                    "The project file could not be loaded.",
                    "src/Todo.Api/Todo.Api.csproj")
            ]);

        var requests = CodeGenerationBuildRepairPlanner.Create(
                CreatePlan(),
                build,
                [CreateTaskResult("small", 1,
                    ["src/Todo.Api/TodoService.cs"])],
                [CreateDecomposition(CreateLeaf())],
                repairCycle: 1);

        var request = requests.Should().ContainSingle().Subject;
        request.IsBuildRepair.Should().BeTrue();
        request.StartingModelTier.Should().Be(
            CodeGenerationWorkflowConstants.MaximumModelTiers);
        request.Task.Artifacts.Should().ContainSingle()
            .Which.Path.Should().Be("src/Todo.Api/Todo.Api.csproj");
        request.Task.ImplementationRequirements.Should().Contain(item =>
            item.Contains("Do not introduce projects"));
    }

    [Fact]
    public void Create_TargetsPlannedSolutionFileFailure()
    {
        var build = new CodeGenerationBuildResult(
            false,
            1,
            "Solution file is invalid.",
            [
                new CodeGenerationBuildDiagnostic(
                    "dotnet",
                    "MSB5010",
                    "Error",
                    "No file format header found.",
                    "/workspace/generated/Todo.sln")
            ]);

        var requests = CodeGenerationBuildRepairPlanner.Create(
            CreatePlan(),
            build,
            [CreateTaskResult("small", 1,
                ["src/Todo.Api/TodoService.cs"])],
            [CreateDecomposition(CreateLeaf())],
            repairCycle: 1);

        var request = requests.Should().ContainSingle().Subject;
        request.Task.Artifacts.Should().ContainSingle()
            .Which.Path.Should().Be("Todo.sln");
        request.Task.Artifacts[0].Kind.Should().Be("DotNetSolution");
    }

    [Fact]
    public void Create_DoesNotRescheduleAResolvedFileFromThePreviousBuild()
    {
        var previous = BuildWithDiagnostic(
            "CS0103",
            "The name 'store' does not exist.",
            "src/Todo.Api/TodoService.cs");
        var current = BuildWithDiagnostic(
            "CS0103",
            "The name 'other' does not exist.",
            "src/Todo.Api/Other.cs");

        var requests = CodeGenerationBuildRepairPlanner.Create(
            CreatePlan(),
            current,
            [
                CreateTaskResult("small", 1,
                    ["src/Todo.Api/TodoService.cs", "src/Todo.Api/Other.cs"])
            ],
            [CreateDecomposition(CreateLeaf())],
            repairCycle: 2,
            previousBuild: previous);

        requests.Should().ContainSingle()
            .Which.Task.Artifacts.Should().ContainSingle()
            .Which.Path.Should().Be("src/Todo.Api/Other.cs");
    }

    [Fact]
    public void Create_RedirectsPersistentReferenceFailureToOwningProject()
    {
        var previous = BuildWithDiagnostic(
            "CS0234",
            "The namespace 'Contracts' could not be found.",
            "src/Todo.Api/TodoService.cs");
        var current = previous with
        {
            Diagnostics =
            [
                previous.Diagnostics[0] with { Line = 27 }
            ]
        };

        var requests = CodeGenerationBuildRepairPlanner.Create(
            CreatePlan(),
            current,
            [CreateTaskResult("small", 1,
                ["src/Todo.Api/TodoService.cs"])],
            [CreateDecomposition(CreateLeaf())],
            repairCycle: 2,
            previousBuild: previous);

        var request = requests.Should().ContainSingle().Subject;
        request.Task.Id.Should().Be("BUILD-ARTIFACTS-REPAIR");
        request.Task.Artifacts.Should().ContainSingle()
            .Which.Path.Should().Be("src/Todo.Api/Todo.Api.csproj");
    }

    [Fact]
    public void Create_DoesNotRepeatUnchangedProjectRepair()
    {
        var previous = BuildWithDiagnostic(
            "CS0234",
            "The namespace 'Contracts' could not be found.",
            "src/Todo.Api/TodoService.cs");
        var priorRepair = CreateTaskResult(
            "large",
            2,
            ["src/Todo.Api/Todo.Api.csproj"]) with
        {
            TaskId = "BUILD-ARTIFACTS-REPAIR",
            IsBuildRepair = true,
            BuildRepairCycle = 2
        };

        var requests = CodeGenerationBuildRepairPlanner.Create(
            CreatePlan(),
            previous,
            [
                CreateTaskResult("small", 1,
                    ["src/Todo.Api/TodoService.cs"]),
                priorRepair
            ],
            [CreateDecomposition(CreateLeaf())],
            repairCycle: 3,
            previousBuild: previous);

        requests.Should().BeEmpty();
    }

    private static CodeGenerationBuildResult BuildWithDiagnostic(
        string code,
        string message,
        string filePath) =>
        new(
            false,
            1,
            "Build failed.",
            [
                new CodeGenerationBuildDiagnostic(
                    "dotnet",
                    code,
                    "Error",
                    message,
                    filePath,
                    "src/Todo.Api/Todo.Api.csproj",
                    10,
                    4)
            ]);

    private static CodeGenerationTaskWorkflowResult CreateTaskResult(
        string model,
        int modelTier,
        IReadOnlyList<string> files) =>
        new(
            "TASK-001-L1",
            true,
            "None",
            null,
            model,
            false,
            [],
            files,
            [])
        {
            ModelTier = modelTier
        };

    private static CodeGenerationDecompositionWorkflowResult
        CreateDecomposition(CodeGenerationLeafTask leaf) =>
        new(
            "TASK-001",
            true,
            "None",
            null,
            "planner",
            new CodeGenerationTaskDecomposition
            {
                ParentTaskId = "TASK-001",
                Status = TaskDecompositionStatus.Ready,
                LeafTasks = [leaf],
                ArchitectureGaps = []
            },
            false,
            []);

    private static CodeGenerationLeafTask CreateLeaf() =>
        new()
        {
            Id = "TASK-001-L1",
            Title = "Implement todo service",
            Objective = "Implement the todo service.",
            ComplexityPoints = 1,
            DependsOn = [],
            ContractIds = ["CONTRACT-001"],
            AcceptanceCriterionIds = ["AC-001"],
            DecisionIds = ["ADR-001"],
            ImplementationRequirements = ["Implement the contract."],
            Artifacts =
            [
                new DecomposedArtifactPlan
                {
                    Path = "src/Todo.Api/TodoService.cs",
                    Kind = "CSharpClass",
                    Namespace = "Todo.Api",
                    TypeNames = ["TodoService"],
                    Requirements = ["Implement ITodoService."]
                },
                new DecomposedArtifactPlan
                {
                    Path = "src/Todo.Api/Other.cs",
                    Kind = "CSharpClass",
                    Namespace = "Todo.Api",
                    TypeNames = ["Other"],
                    Requirements = ["Remain unchanged during repair."]
                }
            ],
            VerificationKinds = ["Compilation"]
        };

    private static CodeGenerationPlan CreatePlan() =>
        new()
        {
            Title = "Todo",
            Summary = "Todo API.",
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
                    Responsibilities = ["Expose todos."],
                    ProjectDependencies = [],
                    Packages = []
                }
            ],
            Modules =
            [
                new PlannedModule
                {
                    Id = "MOD-001",
                    Name = "Todos",
                    ProjectName = "Todo.Api",
                    Responsibilities = ["Manage todos."]
                }
            ],
            Contracts =
            [
                new PlannedContract
                {
                    Id = "CONTRACT-001",
                    Name = "ITodoService",
                    Kind = "Interface",
                    ModuleId = "MOD-001",
                    Purpose = "Manage todos.",
                    Members = ["void Run()"]
                }
            ],
            Decisions =
            [
                new ArchitectureDecision
                {
                    Id = "ADR-001",
                    Title = "Use a service",
                    Decision = "Use TodoService.",
                    Reasons = ["Separation."],
                    AlternativesRejected = [],
                    RelatedPackages = []
                }
            ],
            ArchitectureNotes = [],
            AcceptanceCriteria =
            [
                new PlanAcceptanceCriterion
                {
                    Id = "AC-001",
                    Feature = "Todo",
                    Scenario = "Run service",
                    Given = ["A service"],
                    When = ["It runs"],
                    Then = ["It succeeds"],
                    VerificationKinds = ["Compilation"]
                }
            ],
            Tasks =
            [
                new GenerationTaskPlan
                {
                    Id = "TASK-001",
                    Title = "Implement service",
                    Objective = "Implement service.",
                    ExecutionKind = PlanTaskExecutionKind.CodeGeneration,
                    ModuleId = "MOD-001",
                    ComplexityPoints = 1,
                    ComplexityReasons = ["Small."],
                    DecompositionRecommended = false,
                    EstimatedFiles = 2,
                    DependsOn = [],
                    ContractIds = ["CONTRACT-001"],
                    Relationships = ComponentRelationshipPlan.Empty,
                    DecisionIds = ["ADR-001"],
                    AcceptanceCriterionIds = ["AC-001"],
                    Deliverables = ["TodoService.cs"],
                    VerificationKinds = ["Compilation"]
                }
            ]
        };
}
