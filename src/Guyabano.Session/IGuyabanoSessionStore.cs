namespace Guyabano.Session;

public interface IGuyabanoSessionStore
{
    Task<GuyabanoSession> CreateAsync(
        string repositoryId,
        string workspaceId,
        GuyabanoSessionId? sessionId = null,
        CancellationToken cancellationToken = default);

    Task<GuyabanoSession?> GetAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sessions from newest to oldest without opening their ledger or
    /// workflow stores. This is the operational query used by project/session
    /// pickers and background runtime discovery.
    /// </summary>
    Task<IReadOnlyList<GuyabanoSession>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<GuyabanoSession?> FindByWorkflowRunAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    Task<GuyabanoSession> AttachWorkflowRunAsync(
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-swap the accepted workspace revision. Returns the updated
    /// session when <paramref name="expectedRevision"/> matches the stored value,
    /// or <c>null</c> when another promotion already advanced the revision.
    /// </summary>
    Task<GuyabanoSession?> UpdateWorkspaceRevisionAsync(
        GuyabanoSessionId sessionId,
        string? expectedRevision,
        string replacementRevision,
        CancellationToken cancellationToken = default);
}
