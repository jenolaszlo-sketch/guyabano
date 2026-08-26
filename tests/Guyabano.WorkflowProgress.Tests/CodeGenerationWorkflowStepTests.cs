using FluentAssertions;
using Penghou.Zhinu;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationWorkflowStepTests
{
    [Fact]
    public void ImplementationKeys_AreUniqueForWorkflowVersionTwo()
    {
        var keys = new[]
        {
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

        CodeGenerationWorkflowConstants.WorkflowVersion.Should().Be("2");
        keys.Select(key => key.Value).Should().OnlyHaveUniqueItems();
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
