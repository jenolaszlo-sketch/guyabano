namespace Guyabano.Messaging;

public sealed record WorkflowDiagnostic(
    WorkflowDiagnosticSeverity Severity,
    string Code,
    string Summary,
    IReadOnlyList<string> Details);
