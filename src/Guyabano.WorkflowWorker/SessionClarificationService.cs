using Guyabano.Session;
using Penghou.Cangjie;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Deliberately promotes an accepted clarification into Cangjie knowledge. Raw
/// conversation is never automatically memory; promotion is an explicit action.
/// </summary>
public sealed class SessionClarificationService(
    CangjieRevisionedConceptService cangjieConcepts,
    IGuyabanoSessionStore sessionStore,
    ISessionEventStore sessionEvents)
{
    public async Task<ContextItem> PromoteAsync(
        Guid sessionId,
        string clarificationKey,
        string clarificationText,
        Guid? workflowRunId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clarificationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(clarificationText);

        var session = await sessionStore.GetAsync(
                new GuyabanoSessionId(sessionId),
                cancellationToken)
            .ConfigureAwait(false) ??
            throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");

        var knowledge = await cangjieConcepts.StoreKnowledgeAsync(
            sessionId: sessionId.ToString("D"),
            knowledgeKey: clarificationKey,
            content: clarificationText,
            workflowRunId: workflowRunId?.ToString("D") ?? string.Empty,
            stepKey: "clarification",
            stepRevision: 1,
            repositoryId: session.RepositoryId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await sessionEvents.AppendAsync(new SessionEventRequest(
                session.Id,
                Actor: "guyabano",
                EventType: SessionEventTypes.ClarificationPromoted,
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: workflowRunId,
                CrossSystemRefs: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId.ToString("D"),
                    ["knowledgeKey"] = clarificationKey,
                    ["cangjieItemId"] = knowledge.Id.ToString("D"),
                    ["repositoryId"] = session.RepositoryId
                }))
            .ConfigureAwait(false);

        return knowledge;
    }
}
