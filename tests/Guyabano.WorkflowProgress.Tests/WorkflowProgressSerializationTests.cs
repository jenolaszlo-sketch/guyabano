using System.Text.Json;
using FluentAssertions;
using Guyabano.Messaging;

namespace Guyabano.WorkflowProgressTests;

public sealed class WorkflowProgressSerializationTests
{
    [Fact]
    public void Diagnostics_RoundTripThroughProgressPayloadJson()
    {
        var progress = new WorkflowProgress(
            WorkflowProgressEventType.Completed,
            "Completed",
            "Generation completed with repair.",
            DateTimeOffset.UtcNow,
            Diagnostics:
            [
                new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Warning,
                    "json-repaired",
                    "The model response required JSON repair.",
                    ["inserted missing '}'"])
            ],
            MaximumAttempts: 2,
            WillRetry: true,
            FileChecks:
            [
                new WorkflowGeneratedFileChecks(
                    "Program.cs",
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
            ]);

        var json = JsonSerializer.Serialize(progress);
        var restored =
            JsonSerializer.Deserialize<WorkflowProgress>(json);

        var diagnostic = restored!.Diagnostics
            .Should()
            .ContainSingle()
            .Subject;
        diagnostic.Severity.Should().Be(
            WorkflowDiagnosticSeverity.Warning);
        diagnostic.Details.Should().ContainSingle()
            .Which.Should().Contain("inserted missing '}'");
        restored.MaximumAttempts.Should().Be(2);
        restored.WillRetry.Should().BeTrue();
        restored.IsTerminal.Should().BeFalse();
        var file = restored.FileChecks.Should()
            .ContainSingle().Subject;
        file.Path.Should().Be("Program.cs");
        file.Checks.Should().Contain(check =>
            check.Kind == WorkflowFileCheckKind.Syntax &&
            check.Status == WorkflowFileCheckStatus.Failed);
    }
}
