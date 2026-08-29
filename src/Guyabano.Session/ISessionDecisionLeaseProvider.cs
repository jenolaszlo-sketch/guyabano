namespace Guyabano.Session;

/// <summary>
/// Serializes decisions that must observe one stable session workspace and code
/// graph revision. Providers must coordinate across every process that can
/// approve, promote, or reindex the same session.
/// </summary>
public interface ISessionDecisionLeaseProvider
{
    ValueTask<ISessionDecisionLease> AcquireAsync(
        GuyabanoSessionId sessionId,
        Guid operationId,
        CancellationToken cancellationToken = default);
}

public interface ISessionDecisionLease : IAsyncDisposable
{
    GuyabanoSessionId SessionId { get; }

    Guid OperationId { get; }

    DateTimeOffset AcquiredAt { get; }
}
