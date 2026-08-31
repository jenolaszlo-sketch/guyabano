namespace Guyabano.CodeGeneration.Workflows;

public sealed record BaizeRouterAttemptRecord(
    string EndpointId,
    string? EndpointModel,
    string? EndpointApiStyle,
    string? EndpointProvider,
    string Outcome,
    TimeSpan Duration,
    string? Error,
    string? UnavailableUntil);

public sealed record BaizeRateLimitRecord(
    int? RequestsRemaining,
    int? RequestsLimit,
    DateTimeOffset? RequestsResetAt,
    int? TokensRemaining,
    int? TokensLimit,
    DateTimeOffset? TokensResetAt,
    TimeSpan? RetryAfter,
    DateTimeOffset? UnavailableUntil);

public sealed record BaizeExecutionRecord(
    string SessionId,
    string WorkflowRunId,
    string? WorkflowStepKey,
    int? WorkflowStepRevision,
    Guid? CangjieSnapshotId,
    string? CangjieStrategy,
    string? CangjieStrategyVersion,
    string? CangjieQueryIdentity,
    string? HetuIndexRunId,
    string? HetuIndexIdentity,
    string? WorkspaceRevision,
    string Purpose,
    string? RequestedModel,
    string? Provider,
    string? ActualModel,
    string? ApiStyle,
    IReadOnlyList<BaizeRouterAttemptRecord> RouterAttempts,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? PromptCacheHitTokens,
    int? PromptCacheMissTokens,
    double? TotalDurationMilliseconds,
    double? LoadDurationMilliseconds,
    double? PromptEvaluationDurationMilliseconds,
    double? GenerationDurationMilliseconds,
    double? GenerationTokensPerSecond,
    int? NativeToolCallCount,
    string? FinishReason,
    string? FinishReasonKind,
    bool ContentWasRepaired,
    int ContentRepairAttemptCount,
    BaizeRateLimitRecord? RateLimit,
    string? ResponseId,
    string RequestHash,
    string? ResponseHash,
    bool Succeeded,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? Error)
{
    public int? WorkflowStepAttempt { get; init; }

    public int InvocationOrdinal { get; init; }
}
