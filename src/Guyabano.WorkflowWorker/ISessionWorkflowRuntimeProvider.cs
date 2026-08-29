using Penghou.Zhinu;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Routes workflow operations to the durable Zhinu runtime owned by a session.
/// The lease prevents bounded-cache eviction while an operation is in flight.
/// </summary>
public interface ISessionWorkflowRuntimeProvider
{
    ValueTask<ISessionWorkflowRuntimeLease> AcquireAsync(
        Session.GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default);
}

public interface ISessionWorkflowRuntimeLease : IAsyncDisposable
{
    Session.GuyabanoSessionId SessionId { get; }

    WorkflowEngine Engine { get; }
}

/// <summary>Compatibility adapter for focused tests that own one explicit engine.</summary>
public sealed class FixedSessionWorkflowRuntimeProvider(WorkflowEngine engine) :
    ISessionWorkflowRuntimeProvider
{
    public ValueTask<ISessionWorkflowRuntimeLease> AcquireAsync(
        Session.GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ISessionWorkflowRuntimeLease>(
            new Lease(sessionId, engine));
    }

    private sealed class Lease(
        Session.GuyabanoSessionId sessionId,
        WorkflowEngine engine) : ISessionWorkflowRuntimeLease
    {
        public Session.GuyabanoSessionId SessionId { get; } = sessionId;
        public WorkflowEngine Engine { get; } = engine;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
