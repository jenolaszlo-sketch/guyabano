using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Penghou.Zhinu;

namespace Guyabano.WorkflowProgressTests;

public sealed class SessionWorkflowEventMirrorTests : IAsyncDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-zhinu-mirror-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CommitBeforeCursorCrash_ReplaysIdempotentlyAndAdvancesCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogPath = Path.Combine(rootPath, "catalog.db");
        var catalog = new SqliteGuyabanoSessionCatalog(catalogPath, pooling: false);
        var session = await catalog.CreateAsync("repo", "workspace", cancellationToken: ct);
        var runId = Guid.CreateVersion7();
        await catalog.AttachWorkflowRunAsync(session.Id, runId, ct);
        var registry = new WorkflowRegistry().Register("mirror", "1", new EchoWorkflow());
        await using var runtimes = new SessionWorkflowRuntimeProvider(
            Path.Combine(rootPath, "sessions"),
            registry,
            new UnusedStepResolver(),
            new ZhinuOptions { MaxConcurrentWorkflows = 1 },
            TimeProvider.System,
            NullLoggerFactory.Instance);
        IReadOnlyList<WorkflowEvent> zhinuEvents;
        await using (var runtime = await runtimes.AcquireAsync(session.Id, ct))
        {
            await runtime.Engine.StartAsync("mirror", "1", "value", runId, cancellationToken: ct);
            await runtime.Engine.ExecuteAsync(runId, ct);
            await runtime.Engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
            zhinuEvents = await runtime.Engine.GetEventsAsync(runId, limit: 100, cancellationToken: ct);
        }

        var projections = new SqliteSessionProjectionStore(catalogPath, pooling: false);
        await using var authoritative = new SimingSessionEventStore(
            Path.Combine(rootPath, "session-ledgers"), projectionStore: projections);
        var commitThenThrow = new CommitThenThrowEventStore(authoritative);
        var cursors = new SqliteSessionWorkflowEventMirrorStore(catalogPath, pooling: false);
        var service = new SessionWorkflowEventMirrorService(
            catalog,
            runtimes,
            cursors,
            commitThenThrow,
            TimeProvider.System,
            NullLogger<SessionWorkflowEventMirrorService>.Instance);

        var interrupted = () => service.MirrorPendingAsync(ct);
        await interrupted.Should().ThrowAsync<IOException>();
        (await cursors.GetAsync(session.Id, runId, ct)).Should().BeNull();

        (await service.MirrorPendingAsync(ct)).Should().Be(zhinuEvents.Count);
        var cursor = await cursors.GetAsync(session.Id, runId, ct);
        cursor!.MirroredSequence.Should().Be(zhinuEvents[^1].Sequence);
        var mirrored = (await authoritative.ReadAsync(session.Id, cancellationToken: ct))
            .Where(item => item.EventType == SessionEventTypes.ZhinuEventMirrored)
            .ToArray();
        mirrored.Should().HaveCount(zhinuEvents.Count);
        mirrored.Select(item => item.CrossSystemRefs!["zhinuEventSequence"])
            .Should().OnlyHaveUniqueItems();

        (await service.MirrorPendingAsync(ct)).Should().Be(0);
        (await authoritative.ReadAsync(session.Id, cancellationToken: ct))
            .Should().HaveSameCount(mirrored);
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

    private sealed class UnusedStepResolver : IWorkflowStepResolver
    {
        public ValueTask<IWorkflowStepLease<TStep>> ResolveAsync<TStep>(
            StepImplementationKey implementationKey,
            CancellationToken cancellationToken)
            where TStep : class => throw new NotSupportedException();
    }

    private sealed class CommitThenThrowEventStore(ISessionEventStore inner) :
        ISessionEventStore
    {
        private int throwAfterCommit = 1;

        public async Task<SessionEvent> AppendAsync(
            SessionEventRequest request,
            CancellationToken cancellationToken = default)
        {
            var committed = await inner.AppendAsync(request, cancellationToken);
            if (Interlocked.Exchange(ref throwAfterCommit, 0) == 1)
                throw new IOException("Simulated process loss after Siming commit.");
            return committed;
        }

        public Task<IReadOnlyList<SessionEvent>> ReadAsync(
            GuyabanoSessionId sessionId,
            long afterSequence = 0,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(sessionId, afterSequence, cancellationToken);

        public Task<SessionEventPage> ReadPageAsync(
            SessionEventPageRequest request,
            CancellationToken cancellationToken = default) =>
            inner.ReadPageAsync(request, cancellationToken);

        public Task<SessionEvent?> VerifyChainAsync(
            GuyabanoSessionId sessionId,
            CancellationToken cancellationToken = default) =>
            inner.VerifyChainAsync(sessionId, cancellationToken);
    }
}
