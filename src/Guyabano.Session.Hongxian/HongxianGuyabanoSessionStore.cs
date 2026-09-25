using Penghou.Hongxian;

namespace Guyabano.Session.Hongxian;

/// <summary>
/// Guyabano-owned mapping layer from the product session vocabulary onto the
/// reusable Hongxian kernel: repository/workspace identities become opaque
/// context/resource identities, Zhinu workflow runs become external operation
/// references, and workspace-revision compare-and-swap maps onto Hongxian's
/// revision CAS (conflict exceptions become <c>null</c>). Workspace policy,
/// recovery handlers, and product explanations stay in Guyabano; Hongxian
/// never learns Guyabano product meaning.
/// </summary>
public sealed class HongxianGuyabanoSessionStore(ISessionStore inner) : IGuyabanoSessionStore
{
    /// <summary>External system name recorded for Zhinu workflow runs.</summary>
    public const string ZhinuSystemName = "zhinu";

    public async Task<GuyabanoSession> CreateAsync(
        string repositoryId,
        string workspaceId,
        GuyabanoSessionId? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var created = await inner.CreateAsync(
            repositoryId,
            workspaceId,
            sessionId is null ? null : new SessionId(sessionId.Value.Value),
            cancellationToken).ConfigureAwait(false);
        return Map(created);
    }

    public async Task<GuyabanoSession?> GetAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var found = await inner.GetAsync(
            new SessionId(sessionId.Value),
            cancellationToken).ConfigureAwait(false);
        return found is null ? null : Map(found);
    }

    public async Task<IReadOnlyList<GuyabanoSession>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var sessions = await inner.ListAsync(cancellationToken).ConfigureAwait(false);
        return sessions.Select(Map).ToArray();
    }

    public async Task<GuyabanoSession?> FindByWorkflowRunAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        var found = await inner.FindByExternalOperationAsync(
            new ExternalOperationReference(ZhinuSystemName, workflowRunId),
            cancellationToken).ConfigureAwait(false);
        return found is null ? null : Map(found);
    }

    public async Task<GuyabanoSession> AttachWorkflowRunAsync(
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        var updated = await inner.AttachExternalOperationAsync(
            new SessionId(sessionId.Value),
            new ExternalOperationReference(ZhinuSystemName, workflowRunId),
            cancellationToken).ConfigureAwait(false);
        return Map(updated);
    }

    public async Task<GuyabanoSession?> UpdateWorkspaceRevisionAsync(
        GuyabanoSessionId sessionId,
        string? expectedRevision,
        string replacementRevision,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var updated = await inner.UpdateRevisionAsync(
                new SessionId(sessionId.Value),
                expectedRevision,
                replacementRevision,
                cancellationToken).ConfigureAwait(false);
            return Map(updated);
        }
        catch (SessionRevisionConflictException)
        {
            return null;
        }
    }

    private static GuyabanoSession Map(Penghou.Hongxian.Session session) => new()
    {
        Id = new GuyabanoSessionId(session.Id.Value),
        RepositoryId = session.ContextId,
        WorkspaceId = session.ResourceId,
        CreatedAt = session.CreatedAt,
        WorkflowRunIds = session.ExternalOperations
            .Where(operation => string.Equals(operation.System, ZhinuSystemName, StringComparison.Ordinal))
            .Select(operation => Guid.TryParse(operation.Id, out var runId) ? runId : (Guid?)null)
            .Where(runId => runId.HasValue)
            .Select(runId => runId!.Value)
            .ToArray(),
        CurrentWorkspaceRevision = session.CurrentRevision,
        Version = session.Version,
    };
}
