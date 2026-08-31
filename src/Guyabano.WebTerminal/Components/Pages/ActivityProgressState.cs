using Guyabano.Messaging;

namespace Guyabano.WebTerminal.Components.Pages;

internal sealed class ActivityProgressState(
    WorkflowProgressEntry firstEntry)
{
    private readonly List<WorkflowDiagnostic> diagnostics = [];
    private readonly HashSet<int> attempts = [];
    private int executionCount;

    public string Key { get; } = CreateKey(firstEntry);

    public WorkflowProgressEntry LatestEntry { get; private set; } =
        firstEntry;

    public WorkflowProgress Progress => LatestEntry.Progress;

    public IReadOnlyList<WorkflowDiagnostic> Diagnostics => diagnostics;

    public int AttemptCount => Math.Max(attempts.Count, executionCount);

    public bool WasRetried => AttemptCount > 1;

    public bool SucceededAfterRetry =>
        WasRetried && !IsRunning && !IsFailed;

    public bool IsRunning =>
        Progress.EventType == WorkflowProgressEventType.Started;

    public bool HasWarnings => diagnostics.Any(diagnostic =>
        diagnostic.Severity == WorkflowDiagnosticSeverity.Warning);

    public bool HasErrors => diagnostics.Any(diagnostic =>
        diagnostic.Severity == WorkflowDiagnosticSeverity.Error);

    public bool IsFailed =>
        Progress.EventType is
            WorkflowProgressEventType.Failed or
            WorkflowProgressEventType.Canceled ||
        Progress.Succeeded == false ||
        HasErrors;

    public string VisualState => IsRunning
        ? "running"
        : IsFailed
            ? "failed"
            : HasWarnings
                ? "warning"
                : "succeeded";

    public void Update(WorkflowProgressEntry entry)
    {
        var previous = LatestEntry.Progress;
        if (entry.Progress.EventType == WorkflowProgressEventType.Started)
        {
            executionCount++;
            if (previous.IsTerminal || previous.Succeeded == false)
                DemotePreviousFailureForManualRetry();
        }
        LatestEntry = entry;
        attempts.Add(entry.Progress.Attempt ?? 1);

        foreach (var diagnostic in
                 entry.Progress.Diagnostics ?? [])
        {
            AddDiagnostic(entry.Progress.WillRetry == true &&
                diagnostic.Severity == WorkflowDiagnosticSeverity.Error
                    ? diagnostic with
                    {
                        Severity = WorkflowDiagnosticSeverity.Warning,
                        Code = $"retry-{entry.Progress.Attempt ?? 1}-{diagnostic.Code}",
                        Summary = $"Attempt {entry.Progress.Attempt ?? 1} failed and was retried: {diagnostic.Summary}"
                    }
                    : diagnostic);
        }

        if (entry.Progress.WillRetry == true)
            AddDiagnostic(new WorkflowDiagnostic(
                WorkflowDiagnosticSeverity.Warning,
                $"retry-attempt-{entry.Progress.Attempt ?? 1}",
                $"Attempt {entry.Progress.Attempt ?? 1} did not complete and was retried.",
                [entry.Progress.Message]));
    }

    private void DemotePreviousFailureForManualRetry()
    {
        for (var index = 0; index < diagnostics.Count; index++)
        {
            var diagnostic = diagnostics[index];
            if (diagnostic.Severity != WorkflowDiagnosticSeverity.Error)
                continue;
            diagnostics[index] = diagnostic with
            {
                Severity = WorkflowDiagnosticSeverity.Warning,
                Code = $"previous-{diagnostic.Code}",
                Summary = $"A previous execution failed before the focused retry: {diagnostic.Summary}"
            };
        }
    }

    public void AddDiagnostic(WorkflowDiagnostic diagnostic)
    {
        if (diagnostics.Any(existing =>
                existing.Code == diagnostic.Code))
        {
            return;
        }

        diagnostics.Add(diagnostic);
    }

    public static string CreateKey(
        WorkflowProgressEntry entry) =>
        entry.Progress.ActivityId ?? "workflow";
}
