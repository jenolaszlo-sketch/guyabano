using FluentAssertions;
using Guyabano.Messaging;
using Guyabano.WebTerminal.Components.Pages;

namespace Guyabano.WorkflowProgressTests;

public sealed class ChatRunStateTests
{
    [Fact]
    public void AddProgress_GroupsEventsForTheSameActivityAttempt()
    {
        using var run = new ChatRunState(
            "test prompt",
            CancellationToken.None);
        run.AddProgress(CreateEntry(
            "1-0",
            WorkflowProgressEventType.Started));
        run.AddProgress(CreateEntry(
            "2-0",
            WorkflowProgressEventType.Completed,
            [
                new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Warning,
                    "json-repaired",
                    "JSON repaired.",
                    ["inserted missing '}'"])
            ]));

        var activity = run.Activities.Should().ContainSingle().Subject;
        activity.VisualState.Should().Be("warning");
        activity.Diagnostics.Should().ContainSingle();
        activity.Progress.EventType.Should().Be(
            WorkflowProgressEventType.Completed);
    }

    [Fact]
    public void AddProgress_SeparatesWorkflowRetryAttempts()
    {
        using var run = new ChatRunState(
            "test prompt",
            CancellationToken.None);
        run.AddProgress(CreateEntry(
            "1-0",
            WorkflowProgressEventType.Failed,
            attempt: 1,
            willRetry: true));
        run.AddProgress(CreateEntry(
            "2-0",
            WorkflowProgressEventType.Started,
            attempt: 2));

        run.Activities.Should().HaveCount(2);
        run.Activities[0].VisualState.Should().Be("failed");
        run.Activities[1].VisualState.Should().Be("running");
        run.Activities[0].Progress.IsTerminal.Should().BeFalse();
        run.Activities[1].Progress.MaximumAttempts.Should().Be(2);
    }

    [Fact]
    public void AddProgress_ShowsSuccessfulModelRetryAsSucceeded()
    {
        using var run = new ChatRunState(
            "test prompt",
            CancellationToken.None);
        run.AddProgress(CreateEntry(
            "1-0",
            WorkflowProgressEventType.Failed,
            [
                new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    "invalid-output",
                    "The first attempt returned invalid output.",
                    [])
            ],
            attempt: 1,
            willRetry: true));
        run.AddProgress(CreateEntry(
            "2-0",
            WorkflowProgressEventType.Started,
            attempt: 2));
        run.AddProgress(CreateEntry(
            "3-0",
            WorkflowProgressEventType.Completed,
            attempt: 2));

        run.Activities.Should().HaveCount(2);
        run.Activities[0].VisualState.Should().Be("failed");
        run.Activities[1].VisualState.Should().Be("succeeded");
        run.Activities[1].Progress.Attempt.Should().Be(2);
        run.Activities[1].Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void AddProgress_MergesSyntaxAndCompilationChecksByFile()
    {
        using var run = new ChatRunState(
            "test prompt",
            CancellationToken.None);
        run.AddProgress(CreateEntry(
            "1-0",
            WorkflowProgressEventType.Completed,
            fileChecks:
            [
                CreateFileCheck(
                    WorkflowFileCheckKind.Syntax,
                    WorkflowFileCheckStatus.Passed)
            ]));
        run.AddProgress(CreateEntry(
            "2-0",
            WorkflowProgressEventType.Failed,
            fileChecks:
            [
                CreateFileCheck(
                    WorkflowFileCheckKind.Compilation,
                    WorkflowFileCheckStatus.Failed)
            ]));

        var file = run.FileChecks.Should().ContainSingle().Subject;
        file.Checks.Should().HaveCount(2);
        file.Checks.Should().Contain(check =>
            check.Kind == WorkflowFileCheckKind.Syntax &&
            check.Status == WorkflowFileCheckStatus.Passed);
        file.Checks.Should().Contain(check =>
            check.Kind == WorkflowFileCheckKind.Compilation &&
            check.Status == WorkflowFileCheckStatus.Failed);
    }

    private static WorkflowProgressEntry CreateEntry(
        string entryId,
        WorkflowProgressEventType eventType,
        IReadOnlyList<WorkflowDiagnostic>? diagnostics = null,
        int attempt = 1,
        bool willRetry = false,
        IReadOnlyList<WorkflowGeneratedFileChecks>? fileChecks = null) =>
        new(
            entryId,
            "workflow-1",
            new WorkflowProgress(
                eventType,
                eventType.ToString(),
                "Activity update",
                DateTimeOffset.UtcNow,
                ActivityId: "activity-1",
                Attempt: attempt,
                Succeeded: eventType ==
                    WorkflowProgressEventType.Completed,
                Diagnostics: diagnostics,
                MaximumAttempts: 2,
                WillRetry: willRetry,
                FileChecks: fileChecks));

    private static WorkflowGeneratedFileChecks CreateFileCheck(
        WorkflowFileCheckKind kind,
        WorkflowFileCheckStatus status) =>
        new(
            "src/Guyabano.Generated/Program.cs",
            [new WorkflowFileCheck(kind, status, [])]);
}
