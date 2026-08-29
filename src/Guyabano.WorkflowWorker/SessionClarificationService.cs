using System.Security.Cryptography;
using System.Text;
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
    ISessionEventStore sessionEvents,
    ICrossStoreOperationStore operationStore)
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

        var contentHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(clarificationText)))
            .ToLowerInvariant();
        var operationCorrelation = workflowRunId ?? DeterministicCorrelationId(
            session.Id,
            clarificationKey,
            contentHash);
        var operation = await operationStore.StartAsync(
            new StartCrossStoreOperationRequest(
                session.Id,
                operationCorrelation,
                "clarification-promotion",
                $"clarification:{session.Id}:{clarificationKey}:{contentHash}",
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        try
        {
            var knowledge = await cangjieConcepts.StoreKnowledgeAsync(
                sessionId: sessionId.ToString("D"),
                knowledgeKey: clarificationKey,
                content: clarificationText,
                workflowRunId: operationCorrelation.ToString("D"),
                stepKey: "clarification",
                stepRevision: 1,
                repositoryId: session.RepositoryId,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var promoted = await sessionEvents.AppendAsync(new SessionEventRequest(
                session.Id,
                Actor: "guyabano",
                EventType: SessionEventTypes.ClarificationPromoted,
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: operationCorrelation,
                CrossSystemRefs: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId.ToString("D"),
                    ["operationId"] = operation.Id.ToString(),
                    ["knowledgeKey"] = clarificationKey,
                    ["cangjieItemId"] = knowledge.Id.ToString("D"),
                    ["repositoryId"] = session.RepositoryId,
                    ["contentHash"] = contentHash
                },
                IdempotencyKey:
                    $"{operation.IdempotencyKey}:event:clarification-promoted"),
                cancellationToken).ConfigureAwait(false);

            if (operation.State != CrossStoreOperationState.Completed)
            {
                var participant = "session-ledger:clarification-promoted";
                if (!operation.Participants.Any(item =>
                        item.Participant.Equals(participant, StringComparison.Ordinal)))
                {
                    operation = await operationStore.RecordParticipantAsync(
                        operation.Id,
                        new CrossStoreParticipantReceipt
                        {
                            Participant = participant,
                            IdempotencyKey = operation.ParticipantIdempotencyKey(participant),
                            State = CrossStoreParticipantState.Applied,
                            RecordedAt = promoted.CommittedAt,
                            AfterIdentity = promoted.EventId.ToString("D"),
                            ResultHash = promoted.Hash,
                            RecoveryAction = "Replay the idempotent Siming clarification event append."
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                operation = await operationStore.TransitionAsync(
                    operation.Id,
                    CrossStoreOperationState.Published,
                    DateTimeOffset.UtcNow,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await operationStore.TransitionAsync(
                    operation.Id,
                    CrossStoreOperationState.Completed,
                    DateTimeOffset.UtcNow,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return knowledge;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (operation.State != CrossStoreOperationState.Completed)
            {
                await operationStore.TransitionAsync(
                    operation.Id,
                    CrossStoreOperationState.ReconciliationRequired,
                    DateTimeOffset.UtcNow,
                    $"Clarification promotion failed after operation preparation: {exception.GetType().Name}.",
                    cancellationToken).ConfigureAwait(false);
                await sessionEvents.AppendAsync(
                    new SessionEventRequest(
                        session.Id,
                        Actor: "guyabano",
                        EventType: SessionEventTypes.ClarificationPromotionFailed,
                        OccurredAt: DateTimeOffset.UtcNow,
                        CorrelationId: operationCorrelation,
                        CrossSystemRefs: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["operationId"] = operation.Id.ToString(),
                            ["errorType"] = exception.GetType().Name,
                            ["knowledgeKey"] = clarificationKey
                        },
                        IdempotencyKey:
                            $"{operation.IdempotencyKey}:event:clarification-promotion-failed"),
                    cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }

    private static Guid DeterministicCorrelationId(
        GuyabanoSessionId sessionId,
        string clarificationKey,
        string contentHash)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{sessionId}\n{clarificationKey}\n{contentHash}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
