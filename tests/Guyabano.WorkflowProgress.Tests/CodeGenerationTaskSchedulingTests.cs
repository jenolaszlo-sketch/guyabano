using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationTaskSchedulingTests
{
    [Fact]
    public void OrderCodeGenerationTasks_UsesDependencyOrderNotPlanOrder()
    {
        var plan = CreatePlan(
        [
            CreateTask("T3", ["T2"]),
            CreateTask("T1", ["S"]),
            CreateScaffoldingTask(),
            CreateTask("T2", ["T1"])
        ]);

        var ordered = CodeGenerationWorkflow
            .OrderCodeGenerationTasks(plan);

        ordered.Select(task => task.Id).Should()
            .Equal("T1", "T2", "T3");
    }

    [Fact]
    public void GetReadyCodeGenerationTasks_ReturnsIndependentFanOutWave()
    {
        var plan = CreatePlan(
        [
            CreateTask("T3", ["T1", "T2"]),
            CreateTask("T1", ["S"]),
            CreateScaffoldingTask(),
            CreateTask("T2", ["S"])
        ]);
        var completed = new HashSet<string>(StringComparer.Ordinal)
        {
            "S"
        };

        var ready = CodeGenerationWorkflow.GetReadyCodeGenerationTasks(
            plan,
            completed);

        ready.Select(item => item.Id).Should().Equal("T1", "T2");
    }

    [Fact]
    public void GetReadyCodeGenerationTasks_UnlocksDependentWave()
    {
        var plan = CreatePlan(
        [
            CreateTask("T3", ["T1", "T2"]),
            CreateTask("T1", ["S"]),
            CreateScaffoldingTask(),
            CreateTask("T2", ["S"])
        ]);
        var completed = new HashSet<string>(StringComparer.Ordinal)
        {
            "S",
            "T1",
            "T2"
        };

        var ready = CodeGenerationWorkflow.GetReadyCodeGenerationTasks(
            plan,
            completed);

        ready.Select(item => item.Id).Should().Equal("T3");
    }

    [Fact]
    public void ApplyBuildResult_FailedCompilationFailsWorkflow()
    {
        var generated = new CodeGenerationWorkflowResult(
            true,
            "None",
            null,
            "model",
            false,
            [],
            ["Todo.sln", "src/Todo/Program.cs"],
            []);
        var build = new CodeGenerationBuildResult(
            false,
            1,
            "Build failed with 4 compiler errors.",
            []);

        var result = CodeGenerationWorkflow.ApplyBuildResult(
            generated,
            build);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be("CompilationFailed");
        result.Error.Should().Be(build.Error);
        result.Build.Should().Be(build);
        result.BuildAttempts.Should().Equal(build);
        result.WrittenFiles.Should().Equal(generated.WrittenFiles);
    }

    [Fact]
    public void OrderLeafTasks_UsesSiblingDependencyOrder()
    {
        var decomposition = new CodeGenerationTaskDecomposition
        {
            ParentTaskId = "T1",
            Status = TaskDecompositionStatus.Ready,
            ArchitectureGaps = [],
            LeafTasks =
            [
                CreateLeaf("L3", ["L2"]),
                CreateLeaf("L1", []),
                CreateLeaf("L2", ["L1"])
            ]
        };

        CodeGenerationWorkflow.OrderLeafTasks(decomposition)
            .Select(item => item.Id)
            .Should()
            .Equal("L1", "L2", "L3");
    }

    [Fact]
    public void GetReadyLeafTasks_ReturnsIndependentFanOutWave()
    {
        var decomposition = new CodeGenerationTaskDecomposition
        {
            ParentTaskId = "T1",
            Status = TaskDecompositionStatus.Ready,
            ArchitectureGaps = [],
            LeafTasks =
            [
                CreateLeaf("L3", ["L1", "L2"]),
                CreateLeaf("L1", []),
                CreateLeaf("L2", [])
            ]
        };

        var ready = CodeGenerationWorkflow.GetReadyLeafTasks(
            decomposition,
            new HashSet<string>(StringComparer.Ordinal));

        ready.Select(item => item.Id).Should().Equal("L1", "L2");
    }

    [Fact]
    public void GetReadyLeafTasks_DoesNotRescheduleCompletedLeaves()
    {
        var decomposition = new CodeGenerationTaskDecomposition
        {
            ParentTaskId = "T1",
            Status = TaskDecompositionStatus.Ready,
            ArchitectureGaps = [],
            LeafTasks =
            [
                CreateLeaf("L1", []),
                CreateLeaf("L2", ["L1"])
            ]
        };
        var completed = new HashSet<string>(StringComparer.Ordinal)
        {
            "L1"
        };

        var ready = CodeGenerationWorkflow.GetReadyLeafTasks(
            decomposition,
            completed);

        ready.Select(item => item.Id).Should().Equal("L2");
    }

    private static CodeGenerationPlan CreatePlan(
        List<GenerationTaskPlan> tasks) =>
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
            Projects = [],
            Modules = [],
            Contracts = [],
            Decisions = [],
            ArchitectureNotes = [],
            AcceptanceCriteria = [],
            Tasks = tasks
        };

    private static GenerationTaskPlan CreateScaffoldingTask() =>
        new()
        {
            Id = "S",
            Title = "Scaffold",
            Objective = "Create projects.",
            ExecutionKind = PlanTaskExecutionKind.Scaffolding,
            ComplexityPoints = 1,
            ComplexityReasons = ["Mechanical."],
            DecompositionRecommended = false,
            EstimatedFiles = 3,
            DependsOn = [],
            ContractIds = [],
            Relationships = ComponentRelationshipPlan.Empty,
            DecisionIds = [],
            AcceptanceCriterionIds = [],
            Deliverables = [],
            VerificationKinds = ["Compilation"]
        };

    private static GenerationTaskPlan CreateTask(
        string id,
        List<string> dependencies) =>
        new()
        {
            Id = id,
            Title = id,
            Objective = $"Implement {id}.",
            ExecutionKind = PlanTaskExecutionKind.CodeGeneration,
            ModuleId = "M",
            ComplexityPoints = 1,
            ComplexityReasons = ["Small."],
            DecompositionRecommended = false,
            EstimatedFiles = 1,
            DependsOn = dependencies,
            ContractIds = [],
            Relationships = ComponentRelationshipPlan.Empty,
            DecisionIds = [],
            AcceptanceCriterionIds = [],
            Deliverables = [],
            VerificationKinds = ["Compilation"]
        };

    private static CodeGenerationLeafTask CreateLeaf(
        string id,
        List<string> dependencies) =>
        new()
        {
            Id = id,
            Title = id,
            Objective = $"Implement {id}.",
            ComplexityPoints = 1,
            DependsOn = dependencies,
            ContractIds = [],
            AcceptanceCriterionIds = [],
            DecisionIds = [],
            ImplementationRequirements = ["Implement the leaf."],
            Artifacts =
            [
                new DecomposedArtifactPlan
                {
                    Path = $"src/Todo/{id}.cs",
                    Kind = "CSharpClass",
                    Namespace = "Todo",
                    TypeNames = [id],
                    Requirements = ["Compile."]
                }
            ],
            VerificationKinds = ["Compilation"]
        };
}
