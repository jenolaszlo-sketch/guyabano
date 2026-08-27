namespace Guyabano.Session;

public sealed record GuyabanoSession
{
    public required GuyabanoSessionId Id { get; init; }

    public required string RepositoryId { get; init; }

    public required string WorkspaceId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<Guid> WorkflowRunIds { get; init; } = [];
}
