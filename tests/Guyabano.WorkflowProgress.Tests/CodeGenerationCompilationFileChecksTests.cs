using FluentAssertions;
using Guyabano.CI.Contracts;
using Guyabano.Messaging;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationCompilationFileChecksTests
{
    [Fact]
    public void CreateCompleted_MapsCompilerErrorToSourceFile()
    {
        var checks = CodeGenerationCompilationFileChecks.CreateCompleted(
            [
                "src/Guyabano.Generated/Program.cs",
                "src/Guyabano.Generated/appsettings.json"
            ],
            succeeded: false,
            [
                new CiDiagnostic(
                    "dotnet",
                    "CS0103",
                    CiDiagnosticSeverity.Error,
                    "The name 'builder' does not exist.",
                    "src/Guyabano.Generated/Program.cs",
                    "src/Guyabano.Generated/Guyabano.Generated.csproj",
                    12,
                    18)
            ]);

        checks[0].Checks.Single().Status.Should().Be(
            WorkflowFileCheckStatus.Failed);
        checks[0].Checks.Single().Diagnostics.Single().Details
            .Should().Contain("Location: line 12, column 18");
        checks[1].Checks.Single().Status.Should().Be(
            WorkflowFileCheckStatus.NotApplicable);
    }

    [Fact]
    public void CreateCompleted_LeavesUnrelatedFileNotRunWhenBuildFails()
    {
        var checks = CodeGenerationCompilationFileChecks.CreateCompleted(
            [
                "Guyabano.Generated.sln",
                "src/Guyabano.Generated/Program.cs"
            ],
            succeeded: false,
            [
                new CiDiagnostic(
                    "dotnet",
                    "MSB5010",
                    CiDiagnosticSeverity.Error,
                    "No file format header found.",
                    "Guyabano.Generated.sln")
            ]);

        checks[0].Checks.Single().Status.Should().Be(
            WorkflowFileCheckStatus.Failed);
        checks[1].Checks.Single().Status.Should().Be(
            WorkflowFileCheckStatus.NotRun);
    }
}
