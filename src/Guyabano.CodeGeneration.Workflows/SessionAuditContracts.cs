namespace Guyabano.CodeGeneration.Workflows;

public enum SessionAuditSeverity
{
    Info,
    Warning,
    Error
}

public sealed record SessionAuditFinding(
    SessionAuditSeverity Severity,
    string Category,
    string Message);

public sealed record SessionAuditReport(
    Guid SessionId,
    DateTimeOffset AuditedAt,
    int WorkflowRunsChecked,
    int ArtifactsResolved,
    int SnapshotsResolved,
    IReadOnlyList<SessionAuditFinding> Findings)
{
    public bool IsConsistent => Findings.Count == 0;
}
