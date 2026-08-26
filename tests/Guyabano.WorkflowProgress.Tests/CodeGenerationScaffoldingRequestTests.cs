using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationScaffoldingRequestTests
{
    [Fact]
    public void CreateRequest_MapsStructuredPlanWithoutInterpretingTaskText()
    {
        var plan = new CodeGenerationPlan
        {
            Title = "Todo API",
            Summary = "Todo API plan.",
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
                    Responsibilities = ["Expose endpoints."],
                    ProjectDependencies = [],
                    Packages =
                    [
                        new PackageRequirement
                        {
                            Name = "Example.Package",
                            Version = "1.2.3",
                            Purpose = "Example dependency."
                        }
                    ]
                },
                new PlannedProject
                {
                    Name = "Todo.Tests",
                    Path = "tests/Todo.Tests/Todo.Tests.csproj",
                    Kind = "UnitTests",
                    Role = ProjectRole.Test,
                    TargetFramework = "net10.0",
                    Responsibilities = ["Test behavior."],
                    ProjectDependencies = ["Todo.Api"],
                    Packages = []
                }
            ],
            Modules = [],
            Contracts = [],
            Decisions = [],
            ArchitectureNotes = [],
            AcceptanceCriteria = [],
            Tasks = []
        };

        var request = CodeGenerationScaffoldingActivities.CreateRequest(
            plan,
            "run-123");

        request.RelativePath.Should().Be("run-123");
        request.Solution.Path.Should().Be("Todo.sln");
        request.Projects.Should().HaveCount(2);
        request.Projects[0].Kind.Should().Be("WebApi");
        request.Projects[0].Packages.Should().ContainSingle()
            .Which.Version.Should().Be("1.2.3");
        request.Projects[1].ProjectDependencies.Should()
            .Equal("Todo.Api");
    }
}
