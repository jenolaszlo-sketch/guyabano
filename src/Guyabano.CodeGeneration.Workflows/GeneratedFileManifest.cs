namespace Guyabano.CodeGeneration.Workflows;

public sealed record GeneratedFileManifestEntry(
    string RelativePath,
    string ContentHash,
    long ByteLength,
    string Operation = "Created",
    string? PreviousRelativePath = null,
    string? BeforeHash = null,
    string? AfterHash = null,
    long? BeforeByteLength = null,
    long? AfterByteLength = null);

public sealed record GeneratedFileManifest(
    string SessionId,
    string WorkflowRunId,
    string StepKey,
    int StepRevision,
    string WorkspaceHostPath,
    string WorkspaceCiPath,
    string TaskId,
    IReadOnlyList<GeneratedFileManifestEntry> Files,
    IReadOnlyList<string> SkippedFiles,
    DateTimeOffset CreatedAt,
    string? ParentTaskId = null,
    bool IsBuildRepair = false,
    int BuildRepairCycle = 0,
    string? Model = null,
    int? ModelTier = null,
    CodeGenerationUsage? Usage = null,
    CodeGenerationDiagnostics? Diagnostics = null,
    string? FinishReason = null,
    string? WorkspaceRevisionId = null,
    IReadOnlyList<GeneratedFileManifestEntry>? StaleFiles = null);
