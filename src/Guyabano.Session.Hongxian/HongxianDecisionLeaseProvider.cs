using Penghou.Hongxian;
using HongxianLeases = Penghou.Hongxian.ISessionDecisionLeaseProvider;
using HongxianLease = Penghou.Hongxian.ISessionDecisionLease;

namespace Guyabano.Session.Hongxian;

/// <summary>
/// Guyabano-owned mapping from the product decision-lease surface onto the
/// reusable Hongxian lease provider. The richer Hongxian lease (fencing
/// token, expiry, loss signal, ownership assertion) stays behind the
/// provider; Guyabano sees only session, operation, and acquisition time.
/// </summary>
public sealed class HongxianDecisionLeaseProvider(HongxianLeases inner) : ISessionDecisionLeaseProvider
{
    public async ValueTask<ISessionDecisionLease> AcquireAsync(
        GuyabanoSessionId sessionId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var lease = await inner.AcquireAsync(
            new Penghou.Hongxian.SessionId(sessionId.Value), operationId, cancellationToken).ConfigureAwait(false);
        return new Lease(lease);
    }

    private sealed class Lease(HongxianLease inner) : ISessionDecisionLease
    {
        public GuyabanoSessionId SessionId => new(inner.SessionId.Value);

        public Guid OperationId => inner.OperationId;

        public DateTimeOffset AcquiredAt => inner.AcquiredAt;

        public async ValueTask DisposeAsync() => await inner.DisposeAsync().ConfigureAwait(false);
    }
}
