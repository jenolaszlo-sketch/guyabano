using Guyabano.Messaging;

namespace Guyabano.WebTerminal.Components.Pages;

internal sealed class ActivityProgressState(
    WorkflowProgressEntry firstEntry)
{
    private readonly List<WorkflowDiagnostic> diagnostics = [];

    public string Key { get; } = CreateKey(firstEntry);

    public WorkflowProgressEntry LatestEntry { get; private set; } =
        firstEntry;

    public WorkflowProgress Progress => LatestEntry.Progress;

    public IReadOnlyList<WorkflowDiagnostic> Diagnostics => diagnostics;

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
        LatestEntry = entry;

        foreach (var diagnostic in
                 entry.Progress.Diagnostics ?? [])
        {
            AddDiagnostic(diagnostic);
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
        $"{entry.Progress.ActivityId ?? "workflow"}:" +
        $"{entry.Progress.Attempt ?? 1}";
}
