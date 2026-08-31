using Guyabano.Session;
using Guyabano.Messaging;
using Penghou.Zhinu;

namespace Guyabano.WorkflowWorker;

public sealed record RestartPreview(
    Guid PreviewId,
    Guid WorkflowRunId,
    string TargetStepKey,
    string? WorkspaceRevision,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string> InvalidatedStepKeys,
    IReadOnlyList<string> RerunStepKeys,
    IReadOnlyList<string> ReusableStepKeys,
    IReadOnlyList<RestartPlanStep> Details,
    bool RequiresApproval)
{
    public bool IsNoop => InvalidatedStepKeys.Count == 0;
}

public sealed record RestartApproval(
    Guid ApprovalId,
    Guid RecoveryPlanId,
    Guid PreviewId,
    Guid WorkflowRunId,
    string TargetStepKey,
    string? ApprovedWorkspaceRevision,
    string? ApprovedIndexIdentity,
    string ChangeSetHash,
    string ApprovedBy,
    bool Approved,
    DateTimeOffset ApprovedAt);

public enum RestartOutcomeStatus
{
    Applied,
    RejectedByUser,
    RejectedStale,
    ReconciliationRequired
}

public sealed record RestartOutcome(
    RestartOutcomeStatus Status,
    string Explanation,
    string? SafeWorkspaceRevision,
    Guid? IncidentId = null,
    Guid? RecoveryPlanId = null,
    Guid? RestartOperationId = null,
    long? WorkflowLeaseGeneration = null,
    long? WorkflowEventSequence = null,
    bool? RestartWasApplied = null,
    Guid? ReplacementPreviewId = null)
{
    public bool Applied => Status == RestartOutcomeStatus.Applied;
}

public sealed class CodeGenerationWorkflowRestartService(
    ISessionWorkflowRuntimeProvider workflowRuntimes,
    IGuyabanoSessionStore sessionStore,
    ISessionEventStore sessionEvents,
    ILogger<CodeGenerationWorkflowRestartService> logger,
    SessionRecoveryCoordinator? recoveryCoordinator = null,
    IWorkflowProgressPublisher? progressPublisher = null)
{
    public CodeGenerationWorkflowRestartService(
        WorkflowEngine workflowEngine,
        IGuyabanoSessionStore sessionStore,
        ISessionEventStore sessionEvents,
        ILogger<CodeGenerationWorkflowRestartService> logger,
        SessionRecoveryCoordinator? recoveryCoordinator = null,
        IWorkflowProgressPublisher? progressPublisher = null)
        : this(
            new FixedSessionWorkflowRuntimeProvider(workflowEngine),
            sessionStore,
            sessionEvents,
            logger,
            recoveryCoordinator,
            progressPublisher)
    {
    }

    public async Task<RestartPreview> PreviewAsync(
        Guid workflowRunId,
        string targetStepKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStepKey);

        var session = await sessionStore.FindByWorkflowRunAsync(workflowRunId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow '{workflowRunId}' is not associated with a Guyabano session.");

        await using var workflowRuntime = await workflowRuntimes
            .AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false);

        var plan = await workflowRuntime.Engine.PlanRestartAsync(
            workflowRunId,
            targetStepKey,
            StepRestartMode.Dependents,
            cancellationToken).ConfigureAwait(false);

        var allSteps = await workflowRuntime.Engine.GetStepsAsync(workflowRunId, cancellationToken).ConfigureAwait(false);
        var invalidated = plan.StepsToInvalidate.Select(s => s.StepKey).Distinct(StringComparer.Ordinal).ToArray();
        var invalidatedSet = invalidated.ToHashSet(StringComparer.Ordinal);

        var reusable = allSteps
            .Where(s => !invalidatedSet.Contains(s.StepKey))
            .Select(s => s.StepKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Rerun is same as invalidated for Dependents mode (all invalidated will be rerun)
        var preview = new RestartPreview(
            PreviewId: Guid.CreateVersion7(),
            WorkflowRunId: workflowRunId,
            TargetStepKey: targetStepKey,
            WorkspaceRevision: session.CurrentWorkspaceRevision,
            GeneratedAt: DateTimeOffset.UtcNow,
            InvalidatedStepKeys: invalidated,
            RerunStepKeys: invalidated,
            ReusableStepKeys: reusable,
            Details: plan.StepsToInvalidate,
            RequiresApproval: true);

        await sessionEvents.AppendAsync(new SessionEventRequest(
            session.Id,
            "guyabano",
            SessionEventTypes.InvalidationPreviewed,
            preview.GeneratedAt,
            CorrelationId: workflowRunId,
            CrossSystemRefs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["previewId"] = preview.PreviewId.ToString("D"),
                ["workflowRunId"] = workflowRunId.ToString("D"),
                ["targetStepKey"] = targetStepKey,
                ["workspaceRevision"] = preview.WorkspaceRevision ?? "uninitialized"
            },
            IdempotencyKey: $"restart-preview:{preview.PreviewId:D}"),
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Restart preview for workflow {WorkflowRunId} step {StepKey}: {InvalidatedCount} to invalidate, {ReusableCount} reusable (session {SessionId})",
            workflowRunId, targetStepKey, invalidated.Length, reusable.Length, session.Id);

        await PublishProgressSafelyAsync(
            workflowRunId,
            new WorkflowProgress(
                WorkflowProgressEventType.Completed,
                "Focused retry preview",
                $"Preview ready: {invalidated.Length} step(s) will rerun and {reusable.Length} completed step(s) remain reusable. Approval is required.",
                preview.GeneratedAt,
                RunId: workflowRunId.ToString("D"),
                ActivityId: $"restart-preview:{preview.PreviewId:D}",
                Succeeded: true,
                Metadata: new Dictionary<string, string>
                {
                    ["previewId"] = preview.PreviewId.ToString("D"),
                    ["targetStepKey"] = preview.TargetStepKey,
                    ["invalidatedCount"] = invalidated.Length.ToString(),
                    ["reusableCount"] = reusable.Length.ToString(),
                    ["requiresApproval"] = preview.RequiresApproval.ToString()
                }),
            cancellationToken).ConfigureAwait(false);

        return preview;
    }

    public async Task<RestartOutcome> RestartAsync(
        RestartApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.ChangeSetHash);

        var session = await sessionStore.FindByWorkflowRunAsync(approval.WorkflowRunId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow '{approval.WorkflowRunId}' is not associated with a Guyabano session.");
        if (!approval.Approved)
        {
            var existingOutcome = await FindExistingOutcomeAsync(
                session,
                approval,
                cancellationToken).ConfigureAwait(false);
            if (existingOutcome is not null)
                return existingOutcome;
            return await RejectAndRecoverAsync(
                session,
                approval,
                RestartOutcomeStatus.RejectedByUser,
                "RestartApprovalDenied",
                SessionIncidentSeverity.Warning,
                SessionRecoveryAction.AbandonCandidate,
                SessionEventTypes.ApprovalDenied,
                $"Restart of '{approval.TargetStepKey}' was declined by {approval.ApprovedBy}. No workflow state was changed.",
                cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(
                approval.ApprovedWorkspaceRevision,
                session.CurrentWorkspaceRevision,
                StringComparison.Ordinal))
        {
            var existingOutcome = await FindExistingOutcomeAsync(
                session,
                approval,
                cancellationToken).ConfigureAwait(false);
            if (existingOutcome is not null)
                return existingOutcome;
            return await RejectAndRecoverAsync(
                session,
                approval,
                RestartOutcomeStatus.RejectedStale,
                "StaleWorkspaceRevision",
                SessionIncidentSeverity.Warning,
                SessionRecoveryAction.RefreshPreview,
                SessionEventTypes.PreviewSuperseded,
                $"Approval '{approval.ApprovalId:D}' referenced workspace revision '{approval.ApprovedWorkspaceRevision ?? "uninitialized"}', but the accepted revision is now '{session.CurrentWorkspaceRevision ?? "uninitialized"}'. No workflow state was changed; refresh the impact preview.",
                cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Restarting workflow {WorkflowRunId} at step {StepKey} approved by {ApprovedBy} (session {SessionId})",
            approval.WorkflowRunId, approval.TargetStepKey, approval.ApprovedBy, session.Id);

        var restartActivityId = $"focused-restart:{approval.ApprovalId:D}";
        await PublishProgressSafelyAsync(
            approval.WorkflowRunId,
            new WorkflowProgress(
                WorkflowProgressEventType.Started,
                "Focused retry",
                $"Restart accepted for '{approval.TargetStepKey}'; waiting for Zhinu to apply the selective invalidation.",
                DateTimeOffset.UtcNow,
                RunId: approval.WorkflowRunId.ToString("D"),
                ActivityId: restartActivityId,
                Succeeded: null,
                Metadata: new Dictionary<string, string>
                {
                    ["approvalId"] = approval.ApprovalId.ToString("D"),
                    ["previewId"] = approval.PreviewId.ToString("D"),
                    ["targetStepKey"] = approval.TargetStepKey
                }),
            cancellationToken).ConfigureAwait(false);

        var refs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionId"] = session.Id.ToString(),
            ["workflowRunId"] = approval.WorkflowRunId.ToString("D"),
            ["targetStepKey"] = approval.TargetStepKey,
            ["approvedBy"] = approval.ApprovedBy,
            ["approvalId"] = approval.ApprovalId.ToString("D"),
            ["previewId"] = approval.PreviewId.ToString("D"),
            ["workspaceRevision"] = approval.ApprovedWorkspaceRevision ?? "uninitialized",
            ["indexIdentity"] = approval.ApprovedIndexIdentity ?? "unavailable",
            ["changeSetHash"] = approval.ChangeSetHash
        };
        await sessionEvents.AppendAsync(new SessionEventRequest(
            session.Id,
            Actor: approval.ApprovedBy,
            EventType: SessionEventTypes.ApprovalGranted,
            OccurredAt: approval.ApprovedAt,
            CorrelationId: approval.WorkflowRunId,
            CrossSystemRefs: refs,
            IdempotencyKey: $"approval:{approval.ApprovalId:D}:granted"),
            cancellationToken).ConfigureAwait(false);
        RestartReceipt receipt;
        try
        {
            await using var workflowRuntime = await workflowRuntimes
                .AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false);
            receipt = await workflowRuntime.Engine.RestartStepWithReceiptAsync(
                approval.WorkflowRunId,
                approval.TargetStepKey,
                new RestartStepOptions
                {
                    OperationId = approval.ApprovalId,
                    Mode = StepRestartMode.Dependents,
                    Actor = approval.ApprovedBy,
                    Reason = $"Approved Guyabano restart preview {approval.PreviewId:D} " +
                        $"with change set {approval.ChangeSetHash}."
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PublishProgressSafelyAsync(
                approval.WorkflowRunId,
                new WorkflowProgress(
                    WorkflowProgressEventType.Failed,
                    "Focused retry",
                    $"Zhinu could not apply the focused retry: {exception.Message}",
                    DateTimeOffset.UtcNow,
                    RunId: approval.WorkflowRunId.ToString("D"),
                    ActivityId: restartActivityId,
                    Succeeded: false,
                    Diagnostics:
                    [
                        new WorkflowDiagnostic(
                            WorkflowDiagnosticSeverity.Error,
                            "focused-restart-failed",
                            "The focused retry was not applied.",
                            [exception.Message])
                    ]),
                CancellationToken.None).ConfigureAwait(false);
            var failureRefs = new Dictionary<string, string>(refs,
                StringComparer.Ordinal)
            {
                ["errorType"] = exception.GetType().Name
            };
            var failed = await sessionEvents.AppendAsync(new SessionEventRequest(
                session.Id,
                Actor: "guyabano",
                EventType: SessionEventTypes.RestartFailed,
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: approval.WorkflowRunId,
                CrossSystemRefs: failureRefs,
                IdempotencyKey: $"approval:{approval.ApprovalId:D}:restart-failed")).ConfigureAwait(false);

            var recovery = recoveryCoordinator ?? new SessionRecoveryCoordinator(sessionEvents);
            var explanation =
                $"Zhinu rejected restart of '{approval.TargetStepKey}' with {exception.GetType().Name}. The accepted workspace remains '{session.CurrentWorkspaceRevision ?? "uninitialized"}'; inspect workflow state and reconcile forward before retrying.";
            var incident = new SessionIncident(
                approval.ApprovalId,
                session.Id,
                "RestartExecutionFailed",
                SessionIncidentSeverity.Error,
                explanation,
                DateTimeOffset.UtcNow,
                approval.WorkflowRunId,
                failureRefs,
                failed.EventId);
            var detected = await recovery.DetectAsync(incident, cancellationToken)
                .ConfigureAwait(false);
            var plan = new SessionRecoveryPlan(
                approval.RecoveryPlanId,
                incident.IncidentId,
                session.Id,
                SessionRecoveryAction.ReconcileForward,
                explanation,
                session.CurrentWorkspaceRevision,
                Automatic: false,
                PlannedAt: DateTimeOffset.UtcNow,
                approval.WorkflowRunId,
                failureRefs);
            var planned = await recovery.PlanAsync(plan, detected.EventId, cancellationToken)
                .ConfigureAwait(false);
            await recovery.CompleteAsync(new SessionRecoveryResolution(
                    plan.RecoveryPlanId,
                    incident.IncidentId,
                    session.Id,
                    SessionRecoveryOutcome.ReconciliationRequired,
                    1,
                    explanation,
                    DateTimeOffset.UtcNow,
                    approval.WorkflowRunId,
                    failureRefs),
                planned.EventId,
                cancellationToken).ConfigureAwait(false);
            return new RestartOutcome(
                RestartOutcomeStatus.ReconciliationRequired,
                explanation,
                session.CurrentWorkspaceRevision,
                incident.IncidentId,
                plan.RecoveryPlanId);
        }

        refs["restartOperationId"] = receipt.OperationId.ToString("D");
        refs["workflowLeaseGeneration"] = receipt.LeaseGeneration.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        refs["workflowEventSequence"] = receipt.Event.Sequence.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        refs["workflowEventType"] = receipt.Event.EventType;
        var restartApplied = await sessionEvents.AppendAsync(new SessionEventRequest(
            session.Id,
            Actor: "guyabano",
            EventType: SessionEventTypes.RestartApplied,
            OccurredAt: receipt.Event.Timestamp,
            CorrelationId: approval.WorkflowRunId,
            CrossSystemRefs: refs,
            IdempotencyKey: $"approval:{approval.ApprovalId:D}:restart-applied"),
            cancellationToken).ConfigureAwait(false);
        await ResolveProductOutcomeRecoveryAsync(
            session,
            approval,
            receipt,
            restartApplied.EventId,
            cancellationToken).ConfigureAwait(false);
        await PublishProgressSafelyAsync(
            approval.WorkflowRunId,
            new WorkflowProgress(
                WorkflowProgressEventType.Completed,
                "Focused retry",
                receipt.WasApplied
                    ? $"Focused retry applied to '{approval.TargetStepKey}'. Invalidated workflow work will now rerun."
                    : $"Focused retry for '{approval.TargetStepKey}' was already applied; the durable receipt was replayed.",
                DateTimeOffset.UtcNow,
                RunId: approval.WorkflowRunId.ToString("D"),
                ActivityId: restartActivityId,
                Succeeded: true,
                Metadata: new Dictionary<string, string>
                {
                    ["approvalId"] = approval.ApprovalId.ToString("D"),
                    ["restartOperationId"] = receipt.OperationId.ToString("D"),
                    ["restartWasApplied"] = receipt.WasApplied.ToString()
                }),
            cancellationToken).ConfigureAwait(false);
        return new RestartOutcome(
            RestartOutcomeStatus.Applied,
            receipt.WasApplied
                ? $"Restart of '{approval.TargetStepKey}' was applied."
                : $"Restart of '{approval.TargetStepKey}' was already applied; the authoritative receipt was replayed.",
            session.CurrentWorkspaceRevision,
            RestartOperationId: receipt.OperationId,
            WorkflowLeaseGeneration: receipt.LeaseGeneration,
            WorkflowEventSequence: receipt.Event.Sequence,
            RestartWasApplied: receipt.WasApplied);
    }

    private async Task PublishProgressSafelyAsync(
        Guid workflowRunId,
        WorkflowProgress progress,
        CancellationToken cancellationToken)
    {
        if (progressPublisher is null)
            return;
        try
        {
            await progressPublisher.PublishAsync(
                workflowRunId.ToString("D"),
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to publish focused-restart progress for workflow {WorkflowRunId}.",
                workflowRunId);
        }
    }

    private async Task ResolveProductOutcomeRecoveryAsync(
        GuyabanoSession session,
        RestartApproval approval,
        RestartReceipt receipt,
        Guid restartAppliedEventId,
        CancellationToken cancellationToken)
    {
        var history = await sessionEvents.ReadAsync(
            session.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var pending = history.LastOrDefault(item =>
            item.EventType == SessionEventTypes.UserActionRequired &&
            item.CorrelationId == approval.WorkflowRunId &&
            string.Equals(
                item.CrossSystemRefs?.GetValueOrDefault(
                    "recoveryTargetStepKey"),
                approval.TargetStepKey,
                StringComparison.Ordinal));
        if (pending is null ||
            !Guid.TryParse(
                pending.CrossSystemRefs?.GetValueOrDefault("incidentId"),
                out var incidentId) ||
            !Guid.TryParse(
                pending.CrossSystemRefs?.GetValueOrDefault("recoveryPlanId"),
                out var recoveryPlanId))
        {
            return;
        }
        if (history.Any(item =>
                item.Sequence > pending.Sequence &&
                item.EventType is SessionEventTypes.RecoverySucceeded or
                    SessionEventTypes.RecoveryFailed &&
                item.CrossSystemRefs?.GetValueOrDefault("incidentId") ==
                    incidentId.ToString("D")))
        {
            return;
        }

        var references = new Dictionary<string, string>(
            pending.CrossSystemRefs!,
            StringComparer.Ordinal)
        {
            ["approvalId"] = approval.ApprovalId.ToString("D"),
            ["restartOperationId"] = receipt.OperationId.ToString("D"),
            ["workflowLeaseGeneration"] = receipt.LeaseGeneration.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        };
        var coordinator = recoveryCoordinator ??
            new SessionRecoveryCoordinator(sessionEvents);
        await coordinator.CompleteAsync(new SessionRecoveryResolution(
                recoveryPlanId,
                incidentId,
                session.Id,
                SessionRecoveryOutcome.Recovered,
                Attempt: 1,
                $"Approved focused restart of '{approval.TargetStepKey}' was accepted by Zhinu; previously accepted workspace state remained authoritative.",
                receipt.Event.Timestamp,
                approval.WorkflowRunId,
                references,
                new SessionRecoveryActionReceipt(
                    receipt.OperationId,
                    SessionRecoveryAction.RetryIdempotently,
                    "zhinu-workflow-step",
                    approval.TargetStepKey,
                    $"Zhinu accepted restart operation '{receipt.OperationId:D}' at lease generation {receipt.LeaseGeneration}.",
                    receipt.Event.Timestamp,
                    Verified: true,
                    references)),
            restartAppliedEventId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RestartOutcome> RejectAndRecoverAsync(
        GuyabanoSession session,
        RestartApproval approval,
        RestartOutcomeStatus status,
        string reasonCode,
        SessionIncidentSeverity severity,
        SessionRecoveryAction action,
        string rejectionEventType,
        string explanation,
        CancellationToken cancellationToken)
    {
        var recovery = recoveryCoordinator ?? new SessionRecoveryCoordinator(sessionEvents);
        var references = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionId"] = session.Id.ToString(),
            ["workflowRunId"] = approval.WorkflowRunId.ToString("D"),
            ["targetStepKey"] = approval.TargetStepKey,
            ["approvalId"] = approval.ApprovalId.ToString("D"),
            ["previewId"] = approval.PreviewId.ToString("D"),
            ["approvedWorkspaceRevision"] = approval.ApprovedWorkspaceRevision ?? "uninitialized",
            ["safeWorkspaceRevision"] = session.CurrentWorkspaceRevision ?? "uninitialized",
            ["changeSetHash"] = approval.ChangeSetHash
        };
        var incident = new SessionIncident(
            approval.ApprovalId,
            session.Id,
            reasonCode,
            severity,
            explanation,
            DateTimeOffset.UtcNow,
            approval.WorkflowRunId,
            references);
        var detected = await recovery.DetectAsync(incident, cancellationToken)
            .ConfigureAwait(false);
        var plan = new SessionRecoveryPlan(
            approval.RecoveryPlanId,
            incident.IncidentId,
            session.Id,
            action,
            explanation,
            session.CurrentWorkspaceRevision,
            Automatic: true,
            PlannedAt: DateTimeOffset.UtcNow,
            approval.WorkflowRunId,
            references);
        var planned = await recovery.PlanAsync(plan, detected.EventId, cancellationToken)
            .ConfigureAwait(false);
        RestartPreview? replacement = null;
        var execution = await recovery.ExecuteAsync(
            plan,
            planned.EventId,
            attempt: 1,
            async (attempted, ct) =>
            {
                var rejected = await sessionEvents.AppendAsync(new SessionEventRequest(
                    session.Id,
                    "guyabano",
                    rejectionEventType,
                    DateTimeOffset.UtcNow,
                    CausationId: attempted.EventId,
                    CorrelationId: approval.WorkflowRunId,
                    CrossSystemRefs: references,
                    IdempotencyKey: $"approval:{approval.ApprovalId:D}:{rejectionEventType}"),
                    ct).ConfigureAwait(false);

                if (action == SessionRecoveryAction.AbandonCandidate)
                {
                    var abandoned = await sessionEvents.AppendAsync(new SessionEventRequest(
                        session.Id,
                        "guyabano",
                        SessionEventTypes.CandidateAbandoned,
                        DateTimeOffset.UtcNow,
                        CausationId: rejected.EventId,
                        CorrelationId: approval.WorkflowRunId,
                        CrossSystemRefs: references,
                        IdempotencyKey: $"approval:{approval.ApprovalId:D}:candidate-abandoned"),
                        ct).ConfigureAwait(false);
                    return new SessionRecoveryActionReceipt(
                        abandoned.EventId,
                        action,
                        "restart-preview",
                        approval.PreviewId.ToString("D"),
                        "The denied preview was durably marked as abandoned without mutating workflow state.",
                        abandoned.OccurredAt,
                        Verified: rejected.EventType == SessionEventTypes.ApprovalDenied &&
                            abandoned.EventType == SessionEventTypes.CandidateAbandoned,
                        references);
                }

                if (action == SessionRecoveryAction.RefreshPreview)
                {
                    replacement = await PreviewAsync(
                        approval.WorkflowRunId,
                        approval.TargetStepKey,
                        ct).ConfigureAwait(false);
                    var verified = replacement.WorkflowRunId == approval.WorkflowRunId &&
                        string.Equals(replacement.TargetStepKey, approval.TargetStepKey, StringComparison.Ordinal) &&
                        string.Equals(
                            replacement.WorkspaceRevision,
                            session.CurrentWorkspaceRevision,
                            StringComparison.Ordinal);
                    return new SessionRecoveryActionReceipt(
                        Guid.CreateVersion7(),
                        action,
                        "restart-preview",
                        replacement.PreviewId.ToString("D"),
                        $"Replacement preview '{replacement.PreviewId:D}' targets accepted workspace revision " +
                            $"'{replacement.WorkspaceRevision ?? "uninitialized"}'.",
                        replacement.GeneratedAt,
                        verified,
                        new Dictionary<string, string>(references, StringComparer.Ordinal)
                        {
                            ["replacementPreviewId"] = replacement.PreviewId.ToString("D"),
                            ["replacementWorkspaceRevision"] = replacement.WorkspaceRevision ?? "uninitialized"
                        });
                }

                throw new InvalidOperationException(
                    $"No restart recovery executor is registered for '{action}'.");
            },
            cancellationToken).ConfigureAwait(false);

        if (execution.Outcome != SessionRecoveryOutcome.Recovered)
        {
            return new RestartOutcome(
                RestartOutcomeStatus.ReconciliationRequired,
                execution.Explanation,
                session.CurrentWorkspaceRevision,
                incident.IncidentId,
                plan.RecoveryPlanId);
        }
        return new RestartOutcome(
            status,
            replacement is null
                ? explanation
                : $"{explanation} Replacement preview '{replacement.PreviewId:D}' was generated and must be approved before restart.",
            session.CurrentWorkspaceRevision,
            incident.IncidentId,
            plan.RecoveryPlanId,
            ReplacementPreviewId: replacement?.PreviewId);
    }

    private async Task<RestartOutcome?> FindExistingOutcomeAsync(
        GuyabanoSession session,
        RestartApproval approval,
        CancellationToken cancellationToken)
    {
        var approvalId = approval.ApprovalId.ToString("D");
        var history = await sessionEvents.ReadAsync(session.Id, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var terminal = history.LastOrDefault(item =>
            item.CrossSystemRefs?.GetValueOrDefault("approvalId") == approvalId &&
            item.EventType is SessionEventTypes.RestartApplied or
                SessionEventTypes.RecoverySucceeded or
                SessionEventTypes.RecoveryFailed or
                SessionEventTypes.UserActionRequired);
        if (terminal is null)
            return null;
        if (terminal.EventType == SessionEventTypes.RestartApplied)
            return new RestartOutcome(
                RestartOutcomeStatus.Applied,
                $"Restart of '{approval.TargetStepKey}' was already applied.",
                terminal.CrossSystemRefs?.GetValueOrDefault("workspaceRevision"));
        if (terminal.EventType == SessionEventTypes.RecoveryFailed)
            return new RestartOutcome(
                RestartOutcomeStatus.ReconciliationRequired,
                $"Approval '{approvalId}' already requires forward reconciliation.",
                terminal.CrossSystemRefs?.GetValueOrDefault("safeWorkspaceRevision"),
                approval.ApprovalId,
                approval.RecoveryPlanId);
        var incident = history.LastOrDefault(item =>
            item.EventType == SessionEventTypes.IncidentDetected &&
            item.CrossSystemRefs?.GetValueOrDefault("incidentId") == approvalId);
        var stale = string.Equals(
            incident?.CrossSystemRefs?.GetValueOrDefault("reasonCode"),
            "StaleWorkspaceRevision",
            StringComparison.Ordinal);
        return new RestartOutcome(
            stale ? RestartOutcomeStatus.RejectedStale : RestartOutcomeStatus.RejectedByUser,
            stale
                ? "The approval was already rejected because its workspace revision was stale; its replacement preview can be approved."
                : "The restart approval was already declined and safely resolved.",
            terminal.CrossSystemRefs?.GetValueOrDefault("safeWorkspaceRevision"),
            approval.ApprovalId,
            approval.RecoveryPlanId,
            ReplacementPreviewId: string.Equals(
                terminal.CrossSystemRefs?.GetValueOrDefault("recoveryAction"),
                SessionRecoveryAction.RefreshPreview.ToString(),
                StringComparison.Ordinal) &&
                Guid.TryParse(
                    terminal.CrossSystemRefs?.GetValueOrDefault("recoveryResourceId"),
                    out var replacementPreviewId)
                ? replacementPreviewId
                : null);
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
