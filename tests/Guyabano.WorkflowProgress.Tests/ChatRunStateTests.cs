using FluentAssertions;
using Guyabano.Messaging;
using Guyabano.WebTerminal.Components.Pages;
using Guyabano.WorkflowWorker;

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
    public void AddProgress_GroupsWorkflowRetryAttemptsAsOneLogicalActivity()
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

        var activity = run.Activities.Should().ContainSingle().Subject;
        activity.VisualState.Should().Be("running");
        activity.AttemptCount.Should().Be(2);
        activity.WasRetried.Should().BeTrue();
        activity.Progress.MaximumAttempts.Should().Be(2);
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

        var activity = run.Activities.Should().ContainSingle().Subject;
        activity.VisualState.Should().Be("warning");
        activity.SucceededAfterRetry.Should().BeTrue();
        activity.Progress.Attempt.Should().Be(2);
        activity.Diagnostics.Should().OnlyContain(diagnostic =>
            diagnostic.Severity == WorkflowDiagnosticSeverity.Warning);
    }

    [Fact]
    public void AddProgress_FocusedRetryDemotesPriorTerminalFailure()
    {
        using var run = new ChatRunState(
            "test prompt",
            CancellationToken.None);
        run.AddProgress(CreateEntry(
            "1-0",
            WorkflowProgressEventType.Started));
        run.AddProgress(CreateEntry(
            "2-0",
            WorkflowProgressEventType.Failed,
            [
                new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    "invalid-output",
                    "The decomposition was rejected.",
                    [])
            ]));
        run.AddProgress(CreateEntry(
            "3-0",
            WorkflowProgressEventType.Started));
        run.AddProgress(CreateEntry(
            "4-0",
            WorkflowProgressEventType.Completed));

        var activity = run.Activities.Should().ContainSingle().Subject;
        activity.WasRetried.Should().BeTrue();
        activity.SucceededAfterRetry.Should().BeTrue();
        activity.AttemptCount.Should().Be(2);
        activity.VisualState.Should().Be("warning");
        activity.Diagnostics.Should().OnlyContain(diagnostic =>
            diagnostic.Severity == WorkflowDiagnosticSeverity.Warning);
        activity.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "previous-invalid-output");
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

    [Fact]
    public void SetRestartPreview_MakesApprovalRequirementExplicit()
    {
        using var run = new ChatRunState(
            "test prompt",
            CancellationToken.None);
        var preview = new RestartPreview(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "decomposition/1/TASK-001",
            "revision-1",
            DateTimeOffset.UtcNow,
            ["decomposition/1/TASK-001"],
            ["decomposition/1/TASK-001"],
            ["planning"],
            [],
            RequiresApproval: true);

        run.SetRestartPreview(preview);

        run.RestartPreview.Should().BeSameAs(preview);
        run.RecoveryNotice.Should().Be(
            "Impact preview ready — no workflow work has restarted. " +
            "Confirm below to run the focused retry.");
        run.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void BeginRestartSubmission_DoesNotClaimRestartWasApplied()
    {
        using var run = new ChatRunState(
            "test prompt",
            CancellationToken.None);

        run.BeginRestartSubmission();

        run.Status.Should().Be("Submitting focused retry");
        run.RecoveryNotice.Should().Be(
            "Submitting the approved retry; no workflow work has restarted yet.");
        run.Result.Should().BeNull();
    }

    [Fact]
    public void BeginRestart_ReportsAcceptedRestartBeforeProgressArrives()
    {
        using var run = new ChatRunState(
            "test prompt",
            CancellationToken.None);

        run.BeginRestart();

        run.IsRunning.Should().BeTrue();
        run.Result.Should().BeNull();
        run.Status.Should().Be(
            "Restart accepted; waiting for activity");
        run.RecoveryNotice.Should().Be(
            "Restart accepted; waiting for workflow activity.");
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
