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

    Task<GuyabanoSession?> FindByWorkflowRunAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    Task<GuyabanoSession> AttachWorkflowRunAsync(
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        CancellationToken cancellationToken = default);
}
