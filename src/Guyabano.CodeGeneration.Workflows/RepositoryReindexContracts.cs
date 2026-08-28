namespace Guyabano.CodeGeneration.Workflows;

public sealed record RepositoryReindexRequest(string WorkflowId);

public sealed record RepositoryReindexReceipt(
    string RepositoryId,
    string IndexRunId,
    string IndexIdentity,
    string? SnapshotIdentity,
    bool IsConsistentSnapshot,
    int FilesDiscovered,
    int FilesNew,
    int FilesChanged,
    int FilesUnchanged,
    int FilesDeleted,
    int NodesProduced,
    DateTimeOffset CompletedAt);

public sealed record RepositoryReindexPublicationPayload(
    string RepositoryId,
    string Location,
    string IndexRunId,
    string IndexIdentity,
    string? ProviderSnapshotIdentity,
    bool IsConsistentSnapshot,
    int FilesDiscovered,
    int FilesNew,
    int FilesChanged,
    int FilesUnchanged,
    int FilesDeleted,
    int NodesProduced,
    string SessionId,
    string WorkflowRunId,
    string StepKey,
    int StepRevision,
    DateTimeOffset PublishedAt,
    string? WorkspaceRevisionId);
