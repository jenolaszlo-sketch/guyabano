namespace Guyabano.CodeGeneration.Workflows;

/// <summary>Identifies the logical repository that Guyabano should understand.</summary>
public sealed record RepositoryReference(
    string RepositoryId,
    string Location,
    IReadOnlyList<string>? SymbolSeeds = null);

/// <summary>Binds an indexed repository to the exact source content observed by Hetu.</summary>
public sealed record RepositoryRevision(
    string RepositoryId,
    string Location,
    string WorkspaceRevision,
    string IndexRunId,
    string? ProviderSnapshotIdentity,
    bool IsConsistentSnapshot,
    int FilesDiscovered,
    int FilesRequiringIndexWork,
    IReadOnlyList<string> SourcePaths);

/// <summary>One compact, textual observation selected from the Hetu graph.</summary>
public sealed record RepositoryContextObservation(
    string Key,
    string Content,
    string SourceUri,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>A bounded set of graph observations produced by a versioned strategy.</summary>
public sealed record RepositoryContextSelection(
    RepositoryRevision Revision,
    string Strategy,
    string StrategyVersion,
    IReadOnlyList<RepositoryContextObservation> Observations);

/// <summary>
/// Identifies the immutable Cangjie selection used by planning and carries its
/// exact rendered content through durable workflow history.
/// </summary>
public sealed record RepositoryContextReference(
    Guid SnapshotId,
    RepositoryRevision Revision,
    string Strategy,
    string StrategyVersion,
    string Content,
    int ItemCount);

public sealed record RepositoryIndexRequest(
    RepositoryReference Repository,
    string WorkflowRunId);

public sealed record RepositoryContextSelectionRequest(
    RepositoryRevision Revision,
    IReadOnlyList<string> SymbolSeeds);

public sealed record RepositoryContextCaptureRequest(
    RepositoryContextSelection Selection,
    string WorkflowRunId,
    string QueryText);
