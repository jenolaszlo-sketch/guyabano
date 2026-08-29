namespace Guyabano.Session;

/// <summary>
/// Cross-process decision lease backed by an exclusively opened file. This is
/// the compatibility provider until the SQLite operational catalog owns the
/// same contract transactionally.
/// </summary>
public sealed class FileSystemSessionDecisionLeaseProvider :
    ISessionDecisionLeaseProvider
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly string rootPath;

    public FileSystemSessionDecisionLeaseProvider(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(this.rootPath);
    }

    public async ValueTask<ISessionDecisionLease> AcquireAsync(
        GuyabanoSessionId sessionId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A non-empty operation ID is required.", nameof(operationId));

        var path = Path.Combine(rootPath, $"{sessionId}.decision.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                return new FileDecisionLease(
                    sessionId,
                    operationId,
                    DateTimeOffset.UtcNow,
                    stream);
            }
            catch (IOException)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class FileDecisionLease(
        GuyabanoSessionId sessionId,
        Guid operationId,
        DateTimeOffset acquiredAt,
        FileStream stream) : ISessionDecisionLease
    {
        public GuyabanoSessionId SessionId { get; } = sessionId;

        public Guid OperationId { get; } = operationId;

        public DateTimeOffset AcquiredAt { get; } = acquiredAt;

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
