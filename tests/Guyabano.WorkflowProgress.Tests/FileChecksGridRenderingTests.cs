using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Guyabano.Messaging;
using Guyabano.CodeGeneration.Planning;
using Guyabano.WebTerminal.Components;

namespace Guyabano.WorkflowProgressTests;

public sealed class FileChecksGridRenderingTests
{
    [Fact]
    public async Task GridRendersPathsAndTriStateChecks()
    {
        var files = new[]
        {
            new WorkflowGeneratedFileChecks(
                "Program.cs",
                [
                    new WorkflowFileCheck(
                        WorkflowFileCheckKind.Syntax,
                        WorkflowFileCheckStatus.Passed,
                        []),
                    new WorkflowFileCheck(
                        WorkflowFileCheckKind.Compilation,
                        WorkflowFileCheckStatus.NotRun,
                        [])
                ]),
            new WorkflowGeneratedFileChecks(
                "Broken.cs",
                [
                    new WorkflowFileCheck(
                        WorkflowFileCheckKind.Syntax,
                        WorkflowFileCheckStatus.Failed,
                        [
                            new WorkflowDiagnostic(
                                WorkflowDiagnosticSeverity.Error,
                                "CS1513",
                                "} expected",
                                ["Location: line 4, column 1"])
                        ]),
                    new WorkflowFileCheck(
                        WorkflowFileCheckKind.Compilation,
                        WorkflowFileCheckStatus.NotRun,
                        [])
                ])
        };

        var html = await RenderAsync<FileChecksGrid>(
            new Dictionary<string, object?>
            {
                [nameof(FileChecksGrid.Files)] = files
            });

        html.Should().Contain("Generated file checks");
        html.Should().Contain("Program.cs");
        html.Should().Contain("Broken.cs");
        html.Should().Contain("Syntax check for Program.cs: Passed");
        html.Should().Contain("Syntax check for Broken.cs: Failed");
        html.Should().Contain("Compilation check for Program.cs: NotRun");
    }

    [Fact]
    public async Task DiagnosticDialogRendersFailureReasonAndLocation()
    {
        var selection = new WorkflowFileCheckSelection(
            "Broken.cs",
            new WorkflowFileCheck(
                WorkflowFileCheckKind.Syntax,
                WorkflowFileCheckStatus.Failed,
                [
                    new WorkflowDiagnostic(
                        WorkflowDiagnosticSeverity.Error,
                        "CS1513",
                        "} expected",
                        [
                            "Validator: csharp-syntax",
                            "Location: line 4, column 1"
                        ])
                ]));

        var html = await RenderAsync<FileCheckDiagnosticsDialog>(
            new Dictionary<string, object?>
            {
                [nameof(FileCheckDiagnosticsDialog.Selection)] = selection
            });

        html.Should().Contain("Syntax check failed");
        html.Should().Contain("Broken.cs");
        html.Should().Contain("CS1513");
        html.Should().Contain("} expected");
        html.Should().Contain("Location: line 4, column 1");
    }

    [Fact]
    public async Task PlanViewRendersTaskPointsAndDecompositionState()
    {
        var plan = new CodeGenerationPlan
        {
            Title = "Todo API",
            Summary = "Plan an in-memory todo API.",
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
                    Responsibilities = ["Expose endpoints"],
                    ProjectDependencies = [],
                    Packages = []
                }
            ],
            Modules = [],
            Contracts = [],
            Decisions = [],
            ArchitectureNotes =
            [
                new ArchitectureNote
                {
                    Id = "NOTE-TITLE-LENGTH",
                    Category = ArchitectureNoteCategory.InferredDomainConstraint,
                    Subject = "Todo title length",
                    MissingInformation = "The maximum title length was unspecified.",
                    Decision = "Limit titles to 200 characters.",
                    Reasons = ["Titles should be concise."],
                    Impact = "Long titles are rejected.",
                    AffectedIds = ["TASK-001"],
                    UserOverridable = true
                }
            ],
            AcceptanceCriteria = [],
            Tasks =
            [
                new GenerationTaskPlan
                {
                    Id = "TASK-001",
                    Title = "Implement endpoint",
                    Objective = "Implement todo creation.",
                    ExecutionKind = PlanTaskExecutionKind.CodeGeneration,
                    ModuleId = "MOD-TODOS",
                    ComplexityPoints = 8,
                    ComplexityReasons = ["Crosses components"],
                    DecompositionRecommended = true,
                    EstimatedFiles = 5,
                    DependsOn = [],
                    ContractIds = [],
                    Relationships = ComponentRelationshipPlan.Empty,
                    DecisionIds = [],
                    AcceptanceCriterionIds = [],
                    Deliverables = ["Endpoint"],
                    VerificationKinds = ["IntegrationTest"]
                }
            ]
        };

        var html = await RenderAsync<CodeGenerationPlanView>(
            new Dictionary<string, object?>
            {
                [nameof(CodeGenerationPlanView.Plan)] = plan
            });

        html.Should().Contain("Todo API");
        html.Should().Contain("TASK-001");
        html.Should().Contain("8 pts");
        html.Should().Contain("Further decomposition recommended");
        html.Should().Contain("Inferred architecture notes");
        html.Should().Contain("Limit titles to 200 characters");
    }

    private static async Task<string> RenderAsync<TComponent>(
        IDictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();

        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<TComponent>(
                ParameterView.FromDictionary(parameters));

            return rendered.ToHtmlString();
        });
    }
}
