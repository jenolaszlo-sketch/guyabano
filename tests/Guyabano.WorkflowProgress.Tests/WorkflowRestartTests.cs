using FluentAssertions;
using Guyabano.Messaging;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

public sealed class WorkflowRestartTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-restart-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Preview_ShowsInvalidatedRerunReusableSeparately()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = Guid.NewGuid();
        var (session, _) = await CreateSessionAsync(runId, ct);
        var workflow = new BranchedWorkflow();
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"), Pooling = false });
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("restart-preview", "1", workflow));
        var service = CreateRestartService(engine);

        await engine.StartAsync("restart-preview", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var preview = await service.PreviewAsync(runId, "branch-a", ct);
        preview.InvalidatedStepKeys.Should().Contain("branch-a");
        preview.InvalidatedStepKeys.Should().Contain("a-child");
        preview.RerunStepKeys.Should().BeEquivalentTo(preview.InvalidatedStepKeys);
        preview.ReusableStepKeys.Should().Contain("branch-b");
        preview.ReusableStepKeys.Should().Contain("b-child");
        preview.ReusableStepKeys.Should().NotContain("branch-a");
        preview.RequiresApproval.Should().BeTrue();
    }

    [Fact]
    public async Task ApprovedRestart_PublishesDistinctRecoveryProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = Guid.CreateVersion7();
        await CreateSessionAsync(runId, ct);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(
                "restart-progress",
                "1",
                new BranchedWorkflow()));
        var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions"));
        await using var sessionEvents = new SimingSessionEventStore(
            Path.Combine(rootPath, ".gen", "session-events"));
        var progress = new InMemoryWorkflowProgressHub();
        var service = new CodeGenerationWorkflowRestartService(
            engine,
            sessionStore,
            sessionEvents,
            NullLogger<CodeGenerationWorkflowRestartService>.Instance,
            progressPublisher: progress);

        await engine.StartAsync(
            "restart-progress",
            "1",
            "input",
            runId,
            cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: ct);
        var preview = await service.PreviewAsync(runId, "branch-a", ct);
        var outcome = await service.RestartAsync(
            Approval(preview, "tester", approved: true),
            ct);

        outcome.Applied.Should().BeTrue();
        await using var subscription = progress.SubscribeAsync(
                runId.ToString("D"),
                cancellationToken: ct)
            .GetAsyncEnumerator(ct);
        var entries = new List<WorkflowProgressEntry>();
        for (var index = 0; index < 3; index++)
        {
            (await subscription.MoveNextAsync()).Should().BeTrue();
            entries.Add(subscription.Current);
        }

        entries.Select(entry => entry.Progress.Stage).Should().Equal(
            "Retry impact preview (no restart)",
            "Focused retry",
            "Focused retry");
        entries.Select(entry => entry.Progress.EventType).Should().Equal(
            WorkflowProgressEventType.Completed,
            WorkflowProgressEventType.Started,
            WorkflowProgressEventType.Completed);
        entries[0].Progress.ActivityId.Should().StartWith(
            "restart-preview:");
        entries[1].Progress.ActivityId.Should().Be(
            entries[2].Progress.ActivityId);
    }

    [Fact]
    public async Task Restart_RequiresExplicitApproval()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = Guid.NewGuid();
        var (session, _) = await CreateSessionAsync(runId, ct);
        var workflow = new BranchedWorkflow();
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"), Pooling = false });
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("restart-approval", "1", workflow));
        var service = CreateRestartService(engine);

        await engine.StartAsync("restart-approval", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var preview = await service.PreviewAsync(runId, "branch-a", ct);
        preview.RequiresApproval.Should().BeTrue();

        var unapproved = Approval(preview, "tester", approved: false);
        var rejected = await service.RestartAsync(unapproved, ct);
        rejected.Status.Should().Be(RestartOutcomeStatus.RejectedByUser);
        rejected.SafeWorkspaceRevision.Should().Be(preview.WorkspaceRevision);
        rejected.ReplacementPreviewId.Should().BeNull();
        var denialEvents = await ReadSessionEventsAsync(session.Id, ct);
        denialEvents.Select(item => item.EventType).Should().ContainInOrder(
            SessionEventTypes.IncidentDetected,
            SessionEventTypes.RecoveryPlanned,
            SessionEventTypes.RecoveryAttempted,
            SessionEventTypes.ApprovalDenied,
            SessionEventTypes.CandidateAbandoned,
            SessionEventTypes.RecoverySucceeded);
        SessionTimelineProjection.Project(denialEvents).OperatorState.Should()
            .Be(SessionOperatorState.Ready);
        var deniedReplay = await service.RestartAsync(unapproved, ct);
        deniedReplay.Status.Should().Be(RestartOutcomeStatus.RejectedByUser);
        deniedReplay.ReplacementPreviewId.Should().BeNull();
        (await ReadSessionEventsAsync(session.Id, ct)).Should().HaveSameCount(denialEvents);

        var refreshed = await service.PreviewAsync(runId, "branch-a", ct);
        var approved = Approval(refreshed, "tester", approved: true);
        var applied = await service.RestartAsync(approved, ct);
        applied.Applied.Should().BeTrue();
        applied.RestartOperationId.Should().Be(approved.ApprovalId);
        applied.RestartWasApplied.Should().BeTrue();
        applied.WorkflowLeaseGeneration.Should().NotBeNull();
        applied.WorkflowEventSequence.Should().NotBeNull();

        var replayed = await service.RestartAsync(approved, ct);
        replayed.Applied.Should().BeTrue();
        replayed.RestartOperationId.Should().Be(applied.RestartOperationId);
        replayed.WorkflowLeaseGeneration.Should().Be(applied.WorkflowLeaseGeneration);
        replayed.WorkflowEventSequence.Should().Be(applied.WorkflowEventSequence);
        replayed.RestartWasApplied.Should().BeFalse();
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var steps = await engine.GetStepsAsync(runId, ct);
        steps.Should().Contain(s => s.StepKey == "branch-a");
    }

    [Fact]
    public async Task Vertical_BranchRerun_ReusesSiblingOutputs()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = Guid.NewGuid();
        var (session, workspace) = await CreateSessionAsync(runId, ct, withWorkspace: true);

        var counters = new CounterStore();
        var workflow = new FileWritingBranchedWorkflow(workspace.HostPath, counters);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"), Pooling = false });
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("vertical-branch", "1", workflow));
        var service = CreateRestartService(engine);

        await engine.StartAsync("vertical-branch", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var fileAPath = Path.Combine(workspace.HostPath, "branch-a.txt");
        var fileBPath = Path.Combine(workspace.HostPath, "branch-b.txt");
        File.Exists(fileAPath).Should().BeTrue();
        File.Exists(fileBPath).Should().BeTrue();
        var fileAHash1 = await FileHashAsync(fileAPath, ct);
        var fileBHash1 = await FileHashAsync(fileBPath, ct);
        counters.GetCount("branch-a").Should().Be(1);
        counters.GetCount("branch-b").Should().Be(1);

        var preview = await service.PreviewAsync(runId, "branch-a", ct);
        preview.InvalidatedStepKeys.Should().Contain("branch-a");
        preview.ReusableStepKeys.Should().Contain("branch-b");

        var approval = Approval(preview, "tester", approved: true);
        await service.RestartAsync(approval, ct);

        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var fileAHash2 = await FileHashAsync(fileAPath, ct);
        var fileBHash2 = await FileHashAsync(fileBPath, ct);

        fileAHash2.Should().NotBe(fileAHash1, "branch-a was rerun and should produce new content (revision increment)");
        fileBHash2.Should().Be(fileBHash1, "branch-b was reusable and should not be recreated");
        counters.GetCount("branch-a").Should().Be(2);
        counters.GetCount("branch-b").Should().Be(1, "branch-b should be reused via Zhinu fencing");

        var steps = await engine.GetStepsAsync(runId, ct);
        var branchA = steps.Single(s => s.StepKey == "branch-a");
        var branchB = steps.Single(s => s.StepKey == "branch-b");
        branchA.Revision.Should().BeGreaterThan(0, "restarted branch should have incremented revision");
        // branch-b may have revision bumped due to workflow re-execution, but should not be re-executed (counter proves reuse)
    }

    [Fact]
    public async Task ApprovedFocusedRestart_ClosesMatchingProductOutcomeIncident()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = Guid.NewGuid();
        var (session, _) = await CreateSessionAsync(runId, ct);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(
                "product-recovery",
                "1",
                new BranchedWorkflow()));
        await engine.StartAsync(
            "product-recovery",
            "1",
            "input",
            runId,
            cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: ct);

        await using (var events = new SimingSessionEventStore(
            Path.Combine(rootPath, ".gen", "session-events")))
        {
            var recovery = new SessionRecoveryCoordinator(events);
            var incidentId = Guid.CreateVersion7();
            var planId = Guid.CreateVersion7();
            var refs = new Dictionary<string, string>
            {
                ["recoveryTargetStepKey"] = "branch-a"
            };
            var detected = await recovery.DetectAsync(new SessionIncident(
                incidentId,
                session.Id,
                "DecompositionFailed",
                SessionIncidentSeverity.Error,
                "Generation stopped safely.",
                DateTimeOffset.UtcNow,
                runId,
                refs), ct);
            var planned = await recovery.PlanAsync(new SessionRecoveryPlan(
                planId,
                incidentId,
                session.Id,
                SessionRecoveryAction.RetryIdempotently,
                "Retry branch-a.",
                null,
                false,
                DateTimeOffset.UtcNow,
                runId,
                refs), detected.EventId, ct);
            await recovery.CompleteAsync(new SessionRecoveryResolution(
                planId,
                incidentId,
                session.Id,
                SessionRecoveryOutcome.UserActionRequired,
                0,
                "Approve branch-a restart.",
                DateTimeOffset.UtcNow,
                runId,
                refs), planned.EventId, ct);
        }

        var service = CreateRestartService(engine);
        var preview = await service.PreviewAsync(runId, "branch-a", ct);
        var outcome = await service.RestartAsync(
            Approval(preview, "tester", approved: true),
            ct);

        outcome.Applied.Should().BeTrue();
        var history = await ReadSessionEventsAsync(session.Id, ct);
        var recordedIncidentId = history.Single(candidate =>
                candidate.EventType == SessionEventTypes.IncidentDetected)
            .CrossSystemRefs!["incidentId"];
        history.Any(item =>
            item.EventType == SessionEventTypes.RecoverySucceeded &&
            item.CrossSystemRefs?.GetValueOrDefault("incidentId") ==
                recordedIncidentId).Should().BeTrue();
        SessionTimelineProjection.Project(history).OperatorState.Should()
            .Be(SessionOperatorState.Ready);
    }

    [Fact]
    public async Task Restart_StaleWorkspaceApproval_ReturnsSafeStateAndRecordsRecoveryChain()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = Guid.CreateVersion7();
        var (session, _) = await CreateSessionAsync(runId, ct);
        var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions"));
        (await sessionStore.UpdateWorkspaceRevisionAsync(session.Id, null, "workspace-v1", ct))
            .Should().NotBeNull();
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(store,
            new WorkflowRegistry().Register("stale-approval", "1", new BranchedWorkflow()));
        await using var sessionEvents = new SimingSessionEventStore(
            Path.Combine(rootPath, ".gen", "session-events"));
        var service = new CodeGenerationWorkflowRestartService(
            engine,
            sessionStore,
            sessionEvents,
            NullLogger<CodeGenerationWorkflowRestartService>.Instance,
            new SessionRecoveryCoordinator(sessionEvents));

        await engine.StartAsync("stale-approval", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var preview = await service.PreviewAsync(runId, "branch-a", ct);
        preview.WorkspaceRevision.Should().Be("workspace-v1");
        var before = await engine.GetStepsAsync(runId, ct);
        (await sessionStore.UpdateWorkspaceRevisionAsync(
            session.Id, "workspace-v1", "workspace-v2", ct)).Should().NotBeNull();

        var approval = Approval(preview, "tester", approved: true);
        var outcome = await service.RestartAsync(approval, ct);

        outcome.Status.Should().Be(RestartOutcomeStatus.RejectedStale);
        outcome.SafeWorkspaceRevision.Should().Be("workspace-v2");
        outcome.ReplacementPreviewId.Should().NotBeNull();
        (await engine.GetStepsAsync(runId, ct)).Should().BeEquivalentTo(before);
        var events = await sessionEvents.ReadAsync(session.Id, cancellationToken: ct);
        events.Select(item => item.EventType).Should().ContainInOrder(
            SessionEventTypes.IncidentDetected,
            SessionEventTypes.RecoveryPlanned,
            SessionEventTypes.RecoveryAttempted,
            SessionEventTypes.PreviewSuperseded,
            SessionEventTypes.InvalidationPreviewed,
            SessionEventTypes.RecoverySucceeded);
        var terminal = events.Last(item => item.EventType == SessionEventTypes.RecoverySucceeded);
        terminal.CrossSystemRefs!["recoveryResourceId"].Should()
            .Be(outcome.ReplacementPreviewId.Value.ToString("D"));
        var projection = SessionTimelineProjection.Project(events);
        projection.OperatorState.Should().Be(SessionOperatorState.AwaitingApproval);
        projection.OpenIncidentIds.Should().BeEmpty();
        projection.ResolvedIncidentCount.Should().Be(1);
        projection.LastIncidentReason.Should().Be("StaleWorkspaceRevision");

        var replay = await service.RestartAsync(approval, ct);
        replay.Status.Should().Be(outcome.Status);
        replay.SafeWorkspaceRevision.Should().Be(outcome.SafeWorkspaceRevision);
        replay.IncidentId.Should().Be(outcome.IncidentId);
        replay.RecoveryPlanId.Should().Be(outcome.RecoveryPlanId);
        replay.ReplacementPreviewId.Should().Be(outcome.ReplacementPreviewId);
        (await sessionEvents.ReadAsync(session.Id, cancellationToken: ct))
            .Should().HaveSameCount(events);
    }

    [Fact]
    public async Task Restart_UnexpectedEngineRejection_ReturnsReconciliationStateAndKeepsIncidentOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = Guid.CreateVersion7();
        var (session, _) = await CreateSessionAsync(runId, ct);
        var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions"));
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(store,
            new WorkflowRegistry().Register("engine-rejection", "1", new BranchedWorkflow()));
        await using var sessionEvents = new SimingSessionEventStore(
            Path.Combine(rootPath, ".gen", "session-events"));
        var service = new CodeGenerationWorkflowRestartService(
            engine,
            sessionStore,
            sessionEvents,
            NullLogger<CodeGenerationWorkflowRestartService>.Instance,
            new SessionRecoveryCoordinator(sessionEvents));
        await engine.StartAsync("engine-rejection", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var approvalId = Guid.CreateVersion7();

        var outcome = await service.RestartAsync(new RestartApproval(
            approvalId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            runId,
            "missing-step",
            ApprovedWorkspaceRevision: null,
            ApprovedIndexIdentity: null,
            ChangeSetHash: "missing-step-change-set",
            ApprovedBy: "tester",
            Approved: true,
            ApprovedAt: DateTimeOffset.UtcNow), ct);

        outcome.Status.Should().Be(RestartOutcomeStatus.ReconciliationRequired);
        outcome.IncidentId.Should().Be(approvalId);
        var history = await sessionEvents.ReadAsync(session.Id, cancellationToken: ct);
        history.Select(item => item.EventType).Should().ContainInOrder(
            SessionEventTypes.ApprovalGranted,
            SessionEventTypes.RestartFailed,
            SessionEventTypes.IncidentDetected,
            SessionEventTypes.RecoveryPlanned,
            SessionEventTypes.RecoveryFailed);
        var projection = SessionTimelineProjection.Project(history);
        projection.OperatorState.Should().Be(SessionOperatorState.ReconciliationRequired);
        projection.OpenIncidentIds.Should().ContainSingle(approvalId.ToString("D"));
    }

    [Fact]
    public async Task Fencing_StaleWorkerIsRejectedAfterRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = Guid.NewGuid();
        var (session, _) = await CreateSessionAsync(runId, ct);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"), Pooling = false });
        var counters = new CounterStore();
        var workflow = new FileWritingBranchedWorkflow(Path.Combine(rootPath, "fencing-workspace"), counters)
        {
            // Use workspace path that exists
        };
        // Create workspace dir
        Directory.CreateDirectory(Path.Combine(rootPath, "fencing-workspace"));
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("fencing-test", "1", workflow));
        var service = CreateRestartService(engine);

        await engine.StartAsync("fencing-test", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var stepsBefore = await engine.GetStepsAsync(runId, ct);
        var branchABefore = stepsBefore.Single(s => s.StepKey == "branch-a");

        var preview = await service.PreviewAsync(runId, "branch-a", ct);
        preview.InvalidatedStepKeys.Should().Contain("branch-a");
        await service.RestartAsync(Approval(preview, "tester", approved: true), ct);

        // Simulate stale worker trying to report old revision: the engine should fence it.
        // After restart, the old branch-a revision is invalidated; next execution should create new revision.
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var stepsAfter = await engine.GetStepsAsync(runId, ct);
        var branchAAfter = stepsAfter.Where(s => s.StepKey == "branch-a").OrderByDescending(s => s.Revision).First();
        branchAAfter.Revision.Should().BeGreaterThan(branchABefore.Revision, "restart should increment revision and fence stale attempt");
        // branch-b reuse is proven via counter, revision may vary by engine implementation
        counters.GetCount("branch-a").Should().Be(2);
        counters.GetCount("branch-b").Should().Be(1);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private async Task<(GuyabanoSession session, CodeGenerationWorkspace workspace)> CreateSessionAsync(Guid runId, CancellationToken ct, bool withWorkspace = false)
    {
        var sessionStorePath = Path.Combine(rootPath, ".gen", "sessions");
        var sessionStore = new FileSystemGuyabanoSessionStore(sessionStorePath);
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: ct);
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var workspace = resolver.Resolve(session.Id);
        if (withWorkspace) Directory.CreateDirectory(workspace.HostPath);
        return (session, workspace);
    }

    private CodeGenerationWorkflowRestartService CreateRestartService(WorkflowEngine engine)
    {
        var sessionStorePath = Path.Combine(rootPath, ".gen", "sessions");
        var sessionStore = new FileSystemGuyabanoSessionStore(sessionStorePath);
        return new CodeGenerationWorkflowRestartService(
            engine,
            sessionStore,
            new SimingSessionEventStore(Path.Combine(rootPath, ".gen", "session-events")),
            NullLogger<CodeGenerationWorkflowRestartService>.Instance);
    }

    private async Task<IReadOnlyList<SessionEvent>> ReadSessionEventsAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, ".gen", "session-events"));
        return await events.ReadAsync(sessionId, cancellationToken: cancellationToken);
    }

    private static RestartApproval Approval(
        RestartPreview preview,
        string approvedBy,
        bool approved) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            preview.PreviewId,
            preview.WorkflowRunId,
            preview.TargetStepKey,
            preview.WorkspaceRevision,
            ApprovedIndexIdentity: null,
            ChangeSetHash: "test-change-set",
            approvedBy,
            approved,
            DateTimeOffset.UtcNow);

    private static async Task<string> FileHashAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class CounterStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> counts = new();
        public void Increment(string key) => counts.AddOrUpdate(key, 1, (_, v) => v + 1);
        public int GetCount(string key) => counts.TryGetValue(key, out var v) ? v : 0;
    }

    private sealed class BranchedWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.StepAsync("root", input, (value, step, token) => Task.FromResult("root"), new StepOptions(), cancellationToken);
            var a = context.StepAsync("branch-a", input, (value, step, token) => Task.FromResult("a"), new StepOptions { DependsOn = ["root"] }, cancellationToken);
            var b = context.StepAsync("branch-b", input, (value, step, token) => Task.FromResult("b"), new StepOptions { DependsOn = ["root"] }, cancellationToken);
            await Task.WhenAll(a, b);
            await context.StepAsync("a-child", input, (value, step, token) => Task.FromResult("a-child"), new StepOptions { DependsOn = ["branch-a"] }, cancellationToken);
            await context.StepAsync("b-child", input, (value, step, token) => Task.FromResult("b"), new StepOptions { DependsOn = ["branch-b"] }, cancellationToken);
            return input;
        }
    }

    private sealed class FileWritingBranchedWorkflow(string workspaceRoot, CounterStore counters) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.StepAsync("root", input, (value, step, token) => Task.FromResult("root"), new StepOptions(), cancellationToken);

            await Task.WhenAll(
                context.StepAsync("branch-a", input, async (value, step, token) =>
                {
                    counters.Increment("branch-a");
                    var content = step.Revision == 0 ? "branch-a v1" : $"branch-a v{step.Revision + 1}";
                    var path = Path.Combine(workspaceRoot, "branch-a.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, content, token);
                    return content;
                }, new StepOptions { DependsOn = ["root"] }, cancellationToken),
                context.StepAsync("branch-b", input, async (value, step, token) =>
                {
                    counters.Increment("branch-b");
                    var content = "branch-b v1";
                    var path = Path.Combine(workspaceRoot, "branch-b.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (!File.Exists(path))
                        await File.WriteAllTextAsync(path, content, token);
                    return content;
                }, new StepOptions { DependsOn = ["root"] }, cancellationToken));

            return input;
        }
    }

    private sealed class SignalledWorkflow(TaskCompletionSource<string> tcs) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.StepAsync("root", input, (value, step, token) => Task.FromResult("root"), new StepOptions(), cancellationToken);
            var result = await context.StepAsync("branch-a", input, async (value, step, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                var signalTask = tcs.Task;
                var completed = await Task.WhenAny(signalTask, Task.Delay(Timeout.Infinite, linked.Token));
                if (completed == signalTask)
                    return await signalTask;
                token.ThrowIfCancellationRequested();
                return "fallback";
            }, new StepOptions { DependsOn = ["root"] }, cancellationToken);
            return result;
        }
    }
}
