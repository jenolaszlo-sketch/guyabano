namespace Guyabano.Session;

public sealed record GuyabanoSession
{
    public required GuyabanoSessionId Id { get; init; }

    public required string RepositoryId { get; init; }

    public required string WorkspaceId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<Guid> WorkflowRunIds { get; init; } = [];

    /// <summary>
    /// The accepted (promoted) workspace revision. Identifies the exact current
    /// workspace content; used to fence concurrent staging promotions.
    /// </summary>
    public string? CurrentWorkspaceRevision { get; init; }

    /// <summary>
    /// Monotonic operational-catalog version for optimistic concurrency and UI
    /// refresh tokens. It is not an immutable-ledger sequence.
    /// </summary>
    public long Version { get; init; }
}
