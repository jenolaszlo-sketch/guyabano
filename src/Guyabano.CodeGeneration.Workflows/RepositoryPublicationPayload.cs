namespace Guyabano.CodeGeneration.Workflows;

public sealed record RepositoryPublicationPayload(
    RepositoryRevision Revision,
    string SessionId,
    string WorkflowRunId,
    string StepKey,
    int StepRevision,
    DateTimeOffset PublishedAt,
    string? WorkspaceRevisionId = null);
