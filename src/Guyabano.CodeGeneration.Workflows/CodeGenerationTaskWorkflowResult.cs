using Penghou.Baize;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationTaskWorkflowResult(
    string TaskId,
    bool Succeeded,
    string Failure,
    string? Error,
    string Model,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts,
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> SkippedFiles,
    CodeGenerationUsage? Usage = null,
    CodeGenerationDiagnostics? Diagnostics = null,
    string? FinishReason = null)
{
    public int ModelTier { get; init; } = 1;

    public bool IsBuildRepair { get; init; }

    public int BuildRepairCycle { get; init; }
}
