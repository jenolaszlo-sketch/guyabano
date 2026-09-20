using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Budget remaining at one observation point.</summary>
public sealed record PlanningLoopBudget(
    int IterationsRemaining,
    int MutationsRemaining,
    int StructuralIterationsRemaining,
    int ModelCallsRemaining,
    int? TokensRemaining);

/// <summary>Everything the decider may consider at one iteration.</summary>
public sealed record PlanningLoopObservation(
    string Goal,
    string ExecutionPlan,
    string DesignFingerprint,
    IReadOnlyList<string> FreshArtifactRevisions,
    IReadOnlyList<string> SupersededPins,
    string WorkflowVersion,
    bool WorkflowComplete,
    string EvidenceSummary,
    PlanningLoopBudget Remaining,
    PlanningCheckpoint? PriorCheckpoint);

/// <summary>Terminal outcome of one loop run.</summary>
public sealed record PlanningLoopOutcome(
    PlanningLoopStatus Status,
    string Reason,
    PlanningCheckpoint Checkpoint,
    PlannedExecutionDesign FinalDesign);

/// <summary>One decider invocation: a proposed decision plus its cost.</summary>
public sealed record PlanningDecisionResult(
    bool Succeeded,
    PlanningDecision? Decision,
    int ModelCalls,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Proposes the next loop decision. Production deciders call a model;
/// tests script decisions.
/// </summary>
public interface IPlanningDecider
{
    Task<PlanningDecisionResult> DecideAsync(
        PlanningLoopObservation observation,
        CancellationToken cancellationToken = default);
}

/// <summary>Live workflow state observed by the host.</summary>
public sealed record WorkflowExecutionSnapshot(
    string WorkflowVersion,
    bool IsComplete,
    string EvidenceSummary);

/// <summary>Result of applying one admitted revision to the live workflow.</summary>
public sealed record RevisionExecutionResult(
    string WorkflowVersion,
    string EvidenceSummary,
    int? ReportedTokens);

/// <summary>
/// The Zhinu-side seam: the host observes workflow state and applies
/// admitted revisions. The driver owns everything planning-side; this
/// interface owns version transitions and execution.
/// </summary>
public interface IPlanningExecutionHost
{
    /// <summary>
    /// Creates, registers, starts, and fully executes the initial workflow
    /// revision. Idempotent: returns the current snapshot when already known.
    /// </summary>
    Task<WorkflowExecutionSnapshot> EnsureWorkflowAsync(
        string workflowId,
        string dsl,
        string inputJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-admits and registers one revision without running it, restoring
    /// fingerprint reuse after a restart from the DSL alone.
    /// </summary>
    Task RegisterAsync(
        string workflowId,
        string version,
        string dsl,
        CancellationToken cancellationToken = default);

    Task<WorkflowExecutionSnapshot> ObserveAsync(
        string workflowId,
        CancellationToken cancellationToken = default);

    Task<RevisionExecutionResult> ExecuteRevisionAsync(
        string workflowId,
        string dsl,
        WorkflowPatch patch,
        CancellationToken cancellationToken = default);
}
