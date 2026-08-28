namespace Guyabano.Session;

public static class SessionEventTypes
{
    public const string UserMessage = "user-message";
    public const string AssistantMessage = "assistant-message";
    public const string InputRequested = "input-requested";
    public const string InputProvided = "input-provided";
    public const string ClarificationPromoted = "clarification-promoted";
    public const string ApprovalGranted = "approval-granted";
    public const string ApprovalDenied = "approval-denied";
    public const string WorkflowStarted = "workflow-started";
    public const string WorkflowCompleted = "workflow-completed";
    public const string WorkflowFailed = "workflow-failed";
    public const string StepCompleted = "step-completed";
    public const string ModelInvoked = "model-invoked";
    public const string ArtifactPublished = "artifact-published";
    public const string WorkspacePromoted = "workspace-promoted";
    public const string InvalidationPreviewed = "invalidation-previewed";
    public const string RestartApplied = "restart-applied";
    public const string RestartFailed = "restart-failed";
    public const string CangjieKnowledgePromoted = "cangjie-knowledge-promoted";
    public const string OperationPrepared = "operation-prepared";
    public const string OperationTransitioned = "operation-transitioned";
    public const string OperationParticipantRecorded =
        "operation-participant-recorded";
}

/// <summary>
/// An immutable, ordered session event envelope. Sequence is contiguous within
/// the session's authoritative ledger.
/// </summary>
public sealed record SessionEvent
{
    public required int SchemaVersion { get; init; }

    public required long Sequence { get; init; }

    public required Guid EventId { get; init; }

    public required GuyabanoSessionId SessionId { get; init; }

    public required string Actor { get; init; }

    public required string EventType { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Time assigned atomically by the authoritative ledger.</summary>
    public required DateTimeOffset CommittedAt { get; init; }

    public Guid? CausationId { get; init; }

    public Guid? CorrelationId { get; init; }

    public string? IdempotencyKey { get; init; }

    public IReadOnlyDictionary<string, string>? CrossSystemRefs { get; init; }

    public string? PayloadJson { get; init; }

    public required SessionPayloadSensitivity PayloadSensitivity { get; init; }

    public required SessionPayloadRetention PayloadRetention { get; init; }

    /// <summary>SHA-256 of the original UTF-8 payload when retained or digest-only.</summary>
    public string? PayloadDigest { get; init; }

    public string? PreviousHash { get; init; }

    public string Hash { get; init; } = string.Empty;
}
