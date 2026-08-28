using FluentAssertions;
using Penghou.Zhinu;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.WorkflowWorker;
using Guyabano.Session;
using Guyabano.Session.Sqlite;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationWorkflowStepTests
{
    [Fact]
    public void ImplementationKeys_AreUniqueForWorkflowVersionFive()
    {
        WorkflowStepReference[] steps =
        {
            CodeGenerationWorkflowConstants.StartSessionOperationStep,
            CodeGenerationWorkflowConstants.AdvanceSessionOperationStep,
            CodeGenerationWorkflowConstants.IndexRepositoryStep,
            CodeGenerationWorkflowConstants.SelectRepositoryContextStep,
            CodeGenerationWorkflowConstants.CaptureRepositoryContextStep,
            CodeGenerationWorkflowConstants.PlanStep,
            CodeGenerationWorkflowConstants.DecomposeTaskStep,
            CodeGenerationWorkflowConstants.ReviewArchitectureStep,
            CodeGenerationWorkflowConstants.IntegrateArchitectureStep,
            CodeGenerationWorkflowConstants.ResolveArchitectureGapStep,
            CodeGenerationWorkflowConstants.ScaffoldStep,
            CodeGenerationWorkflowConstants.GenerateTaskStep,
            CodeGenerationWorkflowConstants.BuildStep,
            CodeGenerationWorkflowConstants.LoadCheckpointStep,
            CodeGenerationWorkflowConstants.SaveCheckpointStep
        };

        CodeGenerationWorkflowConstants.WorkflowVersion.Should().Be("5");
        steps.Select(step => step.ImplementationKey.Value)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task SessionOperationSteps_AreRetrySafeAcrossDurableStore()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "guyabano-operation-step-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new FileSystemCrossStoreOperationStore(root);
            using var events = new SimingSessionEventStore(
                Path.Combine(root, "events"));
            var heartbeats = new CodeGenerationActivityHeartbeatStore(
                TimeProvider.System);
            var start = new StartSessionOperationStep(
                store, events, heartbeats);
            var advance = new AdvanceSessionOperationStep(
                store, events, heartbeats);
            var runId = Guid.CreateVersion7();
            var request = new StartSessionOperationRequest(
                GuyabanoSessionId.New(),
                runId,
                "code-generation-run",
                $"{runId:D}:code-generation-run");

            var first = await start.ExecuteAsync(
                CreateContext(runId, 1), request,
                TestContext.Current.CancellationToken);
            var replay = await start.ExecuteAsync(
                CreateContext(runId, 2), request,
                TestContext.Current.CancellationToken);
            replay.Id.Should().Be(first.Id);

            var publication = new AdvanceSessionOperationRequest(
                first.Id,
                CrossStoreOperationState.Published,
                "hetu-reindex-publication",
                CrossStoreParticipantState.Applied,
                AfterIdentity: "index:2",
                ResultHash: "run:2");
            var published = await advance.ExecuteAsync(
                CreateContext(runId, 1), publication,
                TestContext.Current.CancellationToken);
            var publishedReplay = await advance.ExecuteAsync(
                CreateContext(runId, 2), publication,
                TestContext.Current.CancellationToken);

            publishedReplay.State.Should().Be(
                CrossStoreOperationState.Published);
            publishedReplay.Version.Should().Be(published.Version);
            publishedReplay.Participants.Should().ContainSingle();
            var sessionEvents = await events.ReadAsync(
                request.SessionId,
                cancellationToken: TestContext.Current.CancellationToken);
            sessionEvents.Select(item => item.EventType).Should().Equal(
                SessionEventTypes.OperationPrepared,
                SessionEventTypes.OperationTransitioned);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Retry_ReceivesHeartbeatFromPreviousScopedAttempt()
    {
        var step = new RetryProbeStep(
            new CodeGenerationActivityHeartbeatStore(TimeProvider.System));
        var runId = Guid.NewGuid();
        var firstAttempt = CreateContext(runId, attempt: 1);
        var secondAttempt = CreateContext(runId, attempt: 2);

        var first = () => step.ExecuteAsync(
            firstAttempt,
            "value",
            TestContext.Current.CancellationToken);
        await first.Should().ThrowAsync<CodeGenerationActivityException>();

        var result = await step.ExecuteAsync(
            secondAttempt,
            "value",
            TestContext.Current.CancellationToken);

        result.Should().Be("retry-context");
    }

    [Fact]
    public async Task SuccessfulAttempt_ReleasesHeartbeatState()
    {
        var step = new RetryProbeStep(
            new CodeGenerationActivityHeartbeatStore(TimeProvider.System));
        var runId = Guid.NewGuid();
        var first = () => step.ExecuteAsync(
            CreateContext(runId, attempt: 1),
            "value",
            TestContext.Current.CancellationToken);
        await first.Should().ThrowAsync<CodeGenerationActivityException>();
        await step.ExecuteAsync(
            CreateContext(runId, attempt: 2),
            "value",
            TestContext.Current.CancellationToken);

        var next = await step.ExecuteAsync(
            CreateContext(runId, attempt: 3),
            "value",
            TestContext.Current.CancellationToken);

        next.Should().Be("no-context");
    }

    [Fact]
    public async Task NewRevision_DoesNotInheritPreviousRevisionHeartbeat()
    {
        var step = new RetryProbeStep(
            new CodeGenerationActivityHeartbeatStore(TimeProvider.System));
        var runId = Guid.NewGuid();
        var first = () => step.ExecuteAsync(
            CreateContext(runId, attempt: 1, revision: 0),
            "value",
            TestContext.Current.CancellationToken);
        await first.Should().ThrowAsync<CodeGenerationActivityException>();

        var result = await step.ExecuteAsync(
            CreateContext(runId, attempt: 2, revision: 1),
            "value",
            TestContext.Current.CancellationToken);

        result.Should().Be("no-context");
    }

    [Fact]
    public async Task Cancellation_ReleasesHeartbeatState()
    {
        var step = new RetryProbeStep(
            new CodeGenerationActivityHeartbeatStore(TimeProvider.System));
        var runId = Guid.NewGuid();
        var cancelled = () => step.ExecuteAsync(
            CreateContext(runId, attempt: 1),
            "cancel",
            TestContext.Current.CancellationToken);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        var next = await step.ExecuteAsync(
            CreateContext(runId, attempt: 2),
            "value",
            TestContext.Current.CancellationToken);

        next.Should().Be("no-context");
    }

    private static WorkflowStepContext CreateContext(
        Guid runId,
        int attempt,
        int revision = 0) =>
        new(
            runId,
            Guid.NewGuid(),
            "generation/task",
            attempt,
            revision);

    private sealed class RetryProbeStep(
        CodeGenerationActivityHeartbeatStore heartbeatStore) :
        CodeGenerationWorkflowStep<string, string>(heartbeatStore)
    {
        protected override async Task<string> ExecuteCoreAsync(
            string input,
            CancellationToken cancellationToken)
        {
            var context = CodeGenerationActivityExecutionContext.Current;
            if (input == "cancel")
            {
                context.Heartbeat("must-be-released");
                throw new OperationCanceledException("Injected cancellation.");
            }
            if (context.Info.Attempt == 1)
            {
                context.Heartbeat("retry-context");
                throw new CodeGenerationActivityException(
                    "Retry requested.",
                    nonRetryable: false);
            }

            if (context.Info.HeartbeatDetails.Count == 0)
                return "no-context";

            return await context.Info
                .HeartbeatDetailAtAsync<string>(0)
                .ConfigureAwait(false);
        }
    }
}
