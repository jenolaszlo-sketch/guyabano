using Guyabano.Session;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record StartSessionOperationRequest(
    GuyabanoSessionId SessionId,
    Guid WorkflowRunId,
    string Kind,
    string IdempotencyKey);

public sealed record AdvanceSessionOperationRequest(
    CrossStoreOperationId OperationId,
    CrossStoreOperationState TargetState,
    string Participant,
    CrossStoreParticipantState ParticipantState,
    string? BeforeIdentity = null,
    string? AfterIdentity = null,
    string? ResultHash = null,
    string? RecoveryAction = null,
    string? ReconciliationReason = null);

public sealed record RecordProductOutcomeFailureRequest(
    GuyabanoSessionId SessionId,
    Guid WorkflowRunId,
    CrossStoreOperationId OperationId,
    string FailureCode,
    string Explanation,
    string? RecoveryTargetStepKey,
    string UserAction);
