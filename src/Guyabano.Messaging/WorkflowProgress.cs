namespace Guyabano.Messaging;

public sealed record WorkflowProgress(
    WorkflowProgressEventType EventType,
    string Stage,
    string Message,
    DateTimeOffset Timestamp,
    string? RunId = null,
    string? ActivityId = null,
    int? Attempt = null,
    string? Model = null,
    int? GeneratedTokens = null,
    bool? Succeeded = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<WorkflowDiagnostic>? Diagnostics = null,
    int? MaximumAttempts = null,
    bool? WillRetry = null,
    IReadOnlyList<WorkflowGeneratedFileChecks>? FileChecks = null)
{
    public bool IsTerminal =>
        (EventType is
            WorkflowProgressEventType.Completed or
            WorkflowProgressEventType.Failed or
            WorkflowProgressEventType.Canceled) &&
        WillRetry != true;
}
