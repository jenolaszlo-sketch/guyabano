using Guyabano.Session;
using Penghou.Zhinu;

namespace Guyabano.WorkflowWorker;

public sealed record RestartPreview(
    Guid WorkflowRunId,
    string TargetStepKey,
    IReadOnlyList<string> InvalidatedStepKeys,
    IReadOnlyList<string> RerunStepKeys,
    IReadOnlyList<string> ReusableStepKeys,
    IReadOnlyList<RestartPlanStep> Details,
    bool RequiresApproval)
{
    public bool IsNoop => InvalidatedStepKeys.Count == 0;
}

public sealed record RestartApproval(
    Guid WorkflowRunId,
    string TargetStepKey,
    string ApprovedBy,
    bool Approved,
    DateTimeOffset ApprovedAt);

public sealed class CodeGenerationWorkflowRestartService(
    WorkflowEngine workflowEngine,
    IGuyabanoSessionStore sessionStore,
    ISessionEventStore sessionEvents,
    ILogger<CodeGenerationWorkflowRestartService> logger)
{
    public async Task<RestartPreview> PreviewAsync(
        Guid workflowRunId,
        string targetStepKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStepKey);

        var session = await sessionStore.FindByWorkflowRunAsync(workflowRunId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow '{workflowRunId}' is not associated with a Guyabano session.");

        var plan = await workflowEngine.PlanRestartAsync(
            workflowRunId,
            targetStepKey,
            StepRestartMode.Dependents,
            cancellationToken).ConfigureAwait(false);

        var allSteps = await workflowEngine.GetStepsAsync(workflowRunId, cancellationToken).ConfigureAwait(false);
        var invalidated = plan.StepsToInvalidate.Select(s => s.StepKey).Distinct(StringComparer.Ordinal).ToArray();
        var invalidatedSet = invalidated.ToHashSet(StringComparer.Ordinal);

        var reusable = allSteps
            .Where(s => !invalidatedSet.Contains(s.StepKey))
            .Select(s => s.StepKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Rerun is same as invalidated for Dependents mode (all invalidated will be rerun)
        var preview = new RestartPreview(
            WorkflowRunId: workflowRunId,
            TargetStepKey: targetStepKey,
            InvalidatedStepKeys: invalidated,
            RerunStepKeys: invalidated,
            ReusableStepKeys: reusable,
            Details: plan.StepsToInvalidate,
            RequiresApproval: true);

        logger.LogInformation(
            "Restart preview for workflow {WorkflowRunId} step {StepKey}: {InvalidatedCount} to invalidate, {ReusableCount} reusable (session {SessionId})",
            workflowRunId, targetStepKey, invalidated.Length, reusable.Length, session.Id);

        return preview;
    }

    public async Task RestartAsync(
        RestartApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (!approval.Approved)
            throw new InvalidOperationException($"Restart of '{approval.TargetStepKey}' was not approved by {approval.ApprovedBy}.");

        var session = await sessionStore.FindByWorkflowRunAsync(approval.WorkflowRunId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow '{approval.WorkflowRunId}' is not associated with a Guyabano session.");

        logger.LogInformation(
            "Restarting workflow {WorkflowRunId} at step {StepKey} approved by {ApprovedBy} (session {SessionId})",
            approval.WorkflowRunId, approval.TargetStepKey, approval.ApprovedBy, session.Id);

        var refs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionId"] = session.Id.ToString(),
            ["workflowRunId"] = approval.WorkflowRunId.ToString("D"),
            ["targetStepKey"] = approval.TargetStepKey,
            ["approvedBy"] = approval.ApprovedBy
        };
        await sessionEvents.AppendAsync(new SessionEventRequest(
            session.Id,
            Actor: approval.ApprovedBy,
            EventType: SessionEventTypes.ApprovalGranted,
            OccurredAt: approval.ApprovedAt,
            CorrelationId: approval.WorkflowRunId,
            CrossSystemRefs: refs)).ConfigureAwait(false);
        try
        {
            await workflowEngine.RestartStepAsync(
                approval.WorkflowRunId,
                approval.TargetStepKey,
                cancellationToken).ConfigureAwait(false);
            await sessionEvents.AppendAsync(new SessionEventRequest(
                session.Id,
                Actor: "guyabano",
                EventType: SessionEventTypes.RestartApplied,
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: approval.WorkflowRunId,
                CrossSystemRefs: refs)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failureRefs = new Dictionary<string, string>(refs,
                StringComparer.Ordinal)
            {
                ["errorType"] = exception.GetType().Name
            };
            await sessionEvents.AppendAsync(new SessionEventRequest(
                session.Id,
                Actor: "guyabano",
                EventType: SessionEventTypes.RestartFailed,
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: approval.WorkflowRunId,
                CrossSystemRefs: failureRefs)).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RestartPreview> PreviewAndRequireApprovalAsync(
        Guid workflowRunId,
        string targetStepKey,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        var preview = await PreviewAsync(workflowRunId, targetStepKey, cancellationToken).ConfigureAwait(false);
        // Approval is explicit; caller must call RestartAsync with an approved RestartApproval
        // This helper just enforces that preview is shown before restart.
        return preview;
    }
}
