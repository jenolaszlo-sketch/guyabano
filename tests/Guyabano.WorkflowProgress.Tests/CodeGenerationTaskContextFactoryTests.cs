using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationTaskContextFactoryTests
{
    [Fact]
    public async Task CreateAsync_LoadsAssignedProjectAndDependencyFiles()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "guyabano-task-context-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testRoot, "src", "Todo.Api"));
        Directory.CreateDirectory(Path.Combine(testRoot, "tests", "Todo.Tests"));
        Directory.CreateDirectory(Path.Combine(testRoot, "src", "Todo.Api", "obj"));

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(testRoot, "src", "Todo.Api", "Todo.Api.csproj"),
                "<Project />",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(testRoot, "src", "Todo.Api", "TodoService.cs"),
                "namespace Todo.Api;",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(testRoot, "src", "Todo.Api", "obj", "Ignored.cs"),
                "ignored",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(testRoot, "tests", "Todo.Tests", "Todo.Tests.csproj"),
                "<Project />",
                TestContext.Current.CancellationToken);

            var context = await CodeGenerationTaskContextFactory.CreateAsync(
                CreatePlan(),
                "T-TEST",
                "Build a todo API.",
                testRoot,
                TestContext.Current.CancellationToken);

            context.ProjectDirectory.Should().Be("tests/Todo.Tests");
            context.Files.Select(file => file.Path).Should().Contain([
                "src/Todo.Api/Todo.Api.csproj",
                "src/Todo.Api/TodoService.cs",
                "tests/Todo.Tests/Todo.Tests.csproj"
            ]);
            context.Files.Select(file => file.Path).Should()
                .NotContain(path => path.Contains("/obj/"));
            context.ArchitectureNotes.Should().ContainSingle()
                .Which.Decision.Should().Contain("200");
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_LoadsExplicitSolutionRepairArtifact()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "guyabano-task-context-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testRoot, "src", "Todo.Api"));
        Directory.CreateDirectory(Path.Combine(testRoot, "tests", "Todo.Tests"));

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(testRoot, "Todo.sln"),
                "Microsoft Visual Studio Solution File",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(testRoot, "tests", "Todo.Tests", "Todo.Tests.csproj"),
                "<Project />",
                TestContext.Current.CancellationToken);
            var leaf = new CodeGenerationLeafTask
            {
                Id = "BUILD-ARTIFACTS-REPAIR",
                Title = "Repair solution",
                Objective = "Repair Todo.sln.",
                ComplexityPoints = 1,
                DependsOn = [],
                ContractIds = [],
                AcceptanceCriterionIds = [],
                DecisionIds = [],
                ImplementationRequirements = ["Repair the solution."],
                Artifacts =
                [
                    new DecomposedArtifactPlan
                    {
                        Path = "Todo.sln",
                        Kind = "DotNetSolution",
                        Namespace = string.Empty,
                        TypeNames = [],
                        Requirements = ["Keep the solution valid."]
                    }
                ],
                VerificationKinds = ["Compilation"]
            };

            var context = await CodeGenerationTaskContextFactory.CreateAsync(
                CreatePlan(),
                "T-TEST",
                leaf,
                testRoot,
                TestContext.Current.CancellationToken);

            context.AllowBuildArtifacts.Should().BeTrue();
            context.Files.Should().Contain(file =>
                file.Path == "Todo.sln" &&
                file.Content.Contains("Visual Studio Solution File"));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

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
                    Responsibilities = [],
                    ProjectDependencies = [],
                    Packages = []
                },
                new PlannedProject
                {
                    Name = "Todo.Tests",
                    Path = "tests/Todo.Tests/Todo.Tests.csproj",
                    Kind = "UnitTests",
                    Role = ProjectRole.Test,
                    TargetFramework = "net10.0",
                    Responsibilities = [],
                    ProjectDependencies = ["Todo.Api"],
                    Packages = []
                }
            ],
            Modules =
            [
                new PlannedModule
                {
                    Id = "M-TEST",
                    Name = "Tests",
                    ProjectName = "Todo.Tests",
                    Responsibilities = ["Test todo behavior."]
                }
            ],
            Contracts = [],
            Decisions = [],
            ArchitectureNotes =
            [
                new ArchitectureNote
                {
                    Id = "NOTE-TITLE-LENGTH",
                    Category = ArchitectureNoteCategory.InferredDomainConstraint,
                    Subject = "Todo title length",
                    MissingInformation = "The maximum was unspecified.",
                    Decision = "Limit todo titles to 200 characters.",
                    Reasons = ["Titles should remain concise."],
                    Impact = "Long titles are rejected.",
                    AffectedIds = ["T-TEST"],
                    UserOverridable = true
                }
            ],
            AcceptanceCriteria = [],
            Tasks =
            [
                new GenerationTaskPlan
                {
                    Id = "T-TEST",
                    Title = "Write tests",
                    Objective = "Test todo behavior.",
                    ExecutionKind = PlanTaskExecutionKind.CodeGeneration,
                    ModuleId = "M-TEST",
                    ComplexityPoints = 2,
                    ComplexityReasons = ["Small test task."],
                    DecompositionRecommended = false,
                    EstimatedFiles = 1,
                    DependsOn = [],
                    ContractIds = [],
                    Relationships = ComponentRelationshipPlan.Empty,
                    DecisionIds = [],
                    AcceptanceCriterionIds = [],
                    Deliverables = ["TodoServiceTests.cs"],
                    VerificationKinds = ["UnitTest"]
                }
            ]
        };
}
