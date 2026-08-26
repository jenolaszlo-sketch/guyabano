using FluentAssertions;
using Guyabano.CodeGeneration.Validation;
using Guyabano.Llm.CodeGeneration;
using Guyabano.Messaging;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationFileChecksTests
{
    [Fact]
    public void Create_MapsValidationAndAddsPendingCompilationCheck()
    {
        var outcome = new CodeGenerationOutcome(
            Succeeded: true,
            Failure: CodeGenerationFailure.None,
            Error: null,
            Model: "test-model",
            JsonWasRepaired: false,
            JsonRepairAttempts: [],
            WrittenFiles: ["Program.cs", "Sample.sln"],
            SkippedFiles: [])
        {
            FileValidation = new GeneratedFilesValidationResult(
                [
                    new GeneratedFileValidationResult(
                        "Program.cs",
                        WasValidated: true,
                        Diagnostics:
                        [
                            new FileValidationDiagnostic(
                                "csharp-syntax",
                                "CS1513",
                                FileValidationSeverity.Error,
                                "} expected",
                                "Program.cs",
                                4,
                                1)
                        ]),
                    new GeneratedFileValidationResult(
                        "Sample.sln",
                        WasValidated: false,
                        Diagnostics: [])
                ])
        };

        var result = CodeGenerationFileChecks.Create(outcome);

        result.Should().HaveCount(2);
        var program = result.Single(file => file.Path == "Program.cs");
        program.Checks.Single(check =>
                check.Kind == WorkflowFileCheckKind.Syntax)
            .Status.Should().Be(WorkflowFileCheckStatus.Failed);
        program.Checks.Single(check =>
                check.Kind == WorkflowFileCheckKind.Compilation)
            .Status.Should().Be(WorkflowFileCheckStatus.NotRun);
        program.Checks.Single(check =>
                check.Kind == WorkflowFileCheckKind.Syntax)
            .Diagnostics.Should().ContainSingle()
            .Which.Details.Should().Contain(
                "Location: line 4, column 1");

        var solution = result.Single(file => file.Path == "Sample.sln");
        solution.Checks.Single(check =>
                check.Kind == WorkflowFileCheckKind.Syntax)
            .Status.Should().Be(
                WorkflowFileCheckStatus.NotApplicable);
    }
}
