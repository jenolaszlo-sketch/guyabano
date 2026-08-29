using FluentAssertions;
using Guyabano.Session;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Penghou.Zhinu;

namespace Guyabano.WorkflowProgressTests;

public sealed class SessionWorkflowRuntimeProviderTests : IAsyncDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-session-workflow-runtime-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Sessions_OwnIsolatedWorkflowStores_ThatReopenAfterEviction()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionsRoot = Path.Combine(rootPath, "sessions");
        var registry = new WorkflowRegistry()
            .Register("session-child", "1", new EchoWorkflow())
            .Register("session-isolation", "1", new ParentWorkflow());
        await using var provider = new SessionWorkflowRuntimeProvider(
            sessionsRoot,
            registry,
            new UnusedStepResolver(),
            new ZhinuOptions { MaxConcurrentWorkflows = 1 },
            TimeProvider.System,
            NullLoggerFactory.Instance,
            maximumCachedRuntimes: 1);
        var firstSession = GuyabanoSessionId.New();
        var secondSession = GuyabanoSessionId.New();
        var firstRun = Guid.CreateVersion7();
        var secondRun = Guid.CreateVersion7();
        Guid childRun;

        await using (var first = await provider.AcquireAsync(firstSession, ct))
        {
            await first.Engine.StartAsync(
                "session-isolation", "1", "first", firstRun,
                cancellationToken: ct);
            await first.Engine.ExecuteAsync(firstRun, ct);
            (await first.Engine.WaitForCompletionAsync<string>(firstRun, cancellationToken: ct))
                .Should().Be("first");
            var runs = await first.Engine.GetRunsAsync(new RunQuery(), ct);
            childRun = runs.Single(item => item.ParentRunId == firstRun).Id;
        }

        await using (var second = await provider.AcquireAsync(secondSession, ct))
        {
            (await second.Engine.GetRunAsync(firstRun, ct)).Should().BeNull();
            await second.Engine.StartAsync(
                "session-isolation", "1", "second", secondRun,
                cancellationToken: ct);
            await second.Engine.ExecuteAsync(secondRun, ct);
            (await second.Engine.WaitForCompletionAsync<string>(secondRun, cancellationToken: ct))
                .Should().Be("second");
        }

        await using (var reopened = await provider.AcquireAsync(firstSession, ct))
        {
            (await reopened.Engine.GetRunAsync(firstRun, ct)).Should().NotBeNull();
            (await reopened.Engine.GetRunAsync(childRun, ct))!.ParentRunId
                .Should().Be(firstRun);
            (await reopened.Engine.GetRunAsync(secondRun, ct)).Should().BeNull();
            (await reopened.Engine.WaitForCompletionAsync<string>(firstRun, cancellationToken: ct))
                .Should().Be("first");
        }

        File.Exists(Path.Combine(sessionsRoot, firstSession.ToString(), "workflow.db"))
            .Should().BeTrue();
        File.Exists(Path.Combine(sessionsRoot, secondSession.ToString(), "workflow.db"))
            .Should().BeTrue();
        File.Exists(Path.Combine(rootPath, "zhinu.db")).Should().BeFalse();
    }

    [Fact]
    public async Task SeparateSessionEngines_CanExecuteAtTheSameTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var rendezvous = new ExecutionRendezvous();
        var registry = new WorkflowRegistry().Register(
            "concurrent-sessions", "1", new RendezvousWorkflow(rendezvous));
        await using var provider = new SessionWorkflowRuntimeProvider(
            Path.Combine(rootPath, "concurrent-sessions"),
            registry,
            new UnusedStepResolver(),
            new ZhinuOptions { MaxConcurrentWorkflows = 1 },
            TimeProvider.System,
            NullLoggerFactory.Instance,
            maximumCachedRuntimes: 2);
        await using var first = await provider.AcquireAsync(GuyabanoSessionId.New(), ct);
        await using var second = await provider.AcquireAsync(GuyabanoSessionId.New(), ct);
        var firstRun = await first.Engine.StartAsync(
            "concurrent-sessions", "1", "first", cancellationToken: ct);
        var secondRun = await second.Engine.StartAsync(
            "concurrent-sessions", "1", "second", cancellationToken: ct);

        await Task.WhenAll(
            first.Engine.ExecuteAsync(firstRun, ct),
            second.Engine.ExecuteAsync(secondRun, ct)).WaitAsync(TimeSpan.FromSeconds(5), ct);

        rendezvous.MaximumConcurrent.Should().Be(2);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed class EchoWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) => Task.FromResult(input);
    }

    private sealed class ParentWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StartChildAsync<string, string>(
                "child",
                "session-child",
                "1",
                input,
                cancellationToken);
    }

    private sealed class RendezvousWorkflow(ExecutionRendezvous rendezvous) :
        IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            await rendezvous.EnterAsync(cancellationToken);
            return input;
        }
    }

    private sealed class ExecutionRendezvous
    {
        private readonly TaskCompletionSource ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int current;
        private int maximum;

        public int MaximumConcurrent => maximum;

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            var entered = Interlocked.Increment(ref current);
            InterlockedExtensions.Max(ref maximum, entered);
            if (entered == 2)
                ready.TrySetResult();
            await ready.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref current);
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }

    private sealed class UnusedStepResolver : IWorkflowStepResolver
    {
        public ValueTask<IWorkflowStepLease<TStep>> ResolveAsync<TStep>(
            StepImplementationKey implementationKey,
            CancellationToken cancellationToken)
            where TStep : class => throw new NotSupportedException();
    }
}
