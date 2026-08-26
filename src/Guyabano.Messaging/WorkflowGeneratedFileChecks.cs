namespace Guyabano.Messaging;

public sealed record WorkflowGeneratedFileChecks(
    string Path,
    IReadOnlyList<WorkflowFileCheck> Checks);
