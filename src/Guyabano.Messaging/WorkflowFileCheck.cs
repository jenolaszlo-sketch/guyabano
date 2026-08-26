namespace Guyabano.Messaging;

public sealed record WorkflowFileCheck(
    WorkflowFileCheckKind Kind,
    WorkflowFileCheckStatus Status,
    IReadOnlyList<WorkflowDiagnostic> Diagnostics);
