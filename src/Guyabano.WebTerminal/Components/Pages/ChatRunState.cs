using Penghou.Baize;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Messaging;

namespace Guyabano.WebTerminal.Components.Pages;

internal sealed class ChatRunState(
    string prompt,
    CancellationToken parentCancellationToken)
    : IDisposable
{
    private readonly CancellationTokenSource cancellation =
        CancellationTokenSource.CreateLinkedTokenSource(
            parentCancellationToken);

    public string Prompt { get; } = prompt;

    public string? WorkflowId { get; set; }

    public string Status { get; set; } = "Submitting";

    public bool IsRunning { get; set; } = true;

    public string? Error { get; set; }

    public CodeGenerationWorkflowResult? Result { get; set; }

    public List<WorkflowProgressEntry> Progress { get; } = [];

    public List<ActivityProgressState> Activities { get; } = [];

    public List<WorkflowGeneratedFileChecks> FileChecks { get; } = [];

    public CancellationToken CancellationToken =>
        cancellation.Token;

    public void AddProgress(WorkflowProgressEntry entry)
    {
        Progress.Add(entry);
        var key = ActivityProgressState.CreateKey(entry);
        var activity = Activities.Find(candidate =>
            candidate.Key == key);

        if (activity is null)
        {
            activity = new ActivityProgressState(entry);
            Activities.Add(activity);
        }

        activity.Update(entry);
        MergeFileChecks(entry.Progress.FileChecks);
    }

    public void SetResult(CodeGenerationWorkflowResult result)
    {
        Result = result;

        if (!result.JsonWasRepaired ||
            Activities.Count == 0)
        {
            return;
        }

        var details = result.JsonRepairAttempts
            .Where(attempt =>
                attempt.Status is not
                    LlmRepairStatus.Skipped and not
                    LlmRepairStatus.NotApplicable)
            .Select(attempt =>
                $"{attempt.Name}: {attempt.Status}")
            .ToArray();

        Activities[^1].AddDiagnostic(
            new WorkflowDiagnostic(
                WorkflowDiagnosticSeverity.Warning,
                "json-repaired",
                "The model response required JSON repair.",
                details));
    }

    public void CompleteObservation() =>
        cancellation.CancelAfter(TimeSpan.FromSeconds(2));

    public void Dispose() =>
        cancellation.Dispose();

    private void MergeFileChecks(
        IReadOnlyList<WorkflowGeneratedFileChecks>? incoming)
    {
        if (incoming is null)
        {
            return;
        }

        foreach (var file in incoming)
        {
            var index = FileChecks.FindIndex(existing =>
                existing.Path.Equals(
                    file.Path,
                    StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                FileChecks.Add(file);
                continue;
            }

            var checks = FileChecks[index].Checks.ToList();

            foreach (var check in file.Checks)
            {
                var checkIndex = checks.FindIndex(existing =>
                    existing.Kind == check.Kind);

                if (checkIndex < 0)
                {
                    checks.Add(check);
                }
                else
                {
                    checks[checkIndex] = check;
                }
            }

            FileChecks[index] = new WorkflowGeneratedFileChecks(
                file.Path,
                checks);
        }
    }
}
