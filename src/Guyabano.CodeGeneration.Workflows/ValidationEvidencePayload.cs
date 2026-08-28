namespace Guyabano.CodeGeneration.Workflows;

public sealed record ValidationEvidencePayload(
    CodeGenerationBuildResult BuildResult,
    string SessionId,
    string WorkflowRunId,
    string StepKey,
    int StepRevision,
    string WorkspaceHostPath,
    string WorkspaceCiPath,
    IReadOnlyList<string> EvaluatedFiles,
    DateTimeOffset PublishedAt,
    string? WorkspaceRevisionId = null);
