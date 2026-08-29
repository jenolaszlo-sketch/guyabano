namespace Guyabano.Session;

/// <summary>
/// Durable delivery position for one Zhinu workflow run mirrored into its
/// owning session ledger. Zhinu's sequence is authoritative for this cursor.
/// </summary>
public sealed record SessionWorkflowEventMirrorCursor(
    GuyabanoSessionId SessionId,
    Guid WorkflowRunId,
    long MirroredSequence,
    DateTimeOffset UpdatedAt);

public interface ISessionWorkflowEventMirrorStore
{
    Task<SessionWorkflowEventMirrorCursor?> GetAsync(
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    Task<SessionWorkflowEventMirrorCursor> AdvanceAsync(
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        long expectedSequence,
        long mirroredSequence,
        CancellationToken cancellationToken = default);
}
