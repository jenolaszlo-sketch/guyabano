using Penghou.Baize;
using Guyabano.CodeGeneration.Planning;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationDecompositionWorkflowResult(
    string ParentTaskId,
    bool Succeeded,
    string Failure,
    string? Error,
    string Model,
    CodeGenerationTaskDecomposition? Decomposition,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts,
    CodeGenerationUsage? Usage = null,
    CodeGenerationDiagnostics? Diagnostics = null,
    string? FinishReason = null,
    ArtifactReference? Artifact = null)
{
    public int ArchitectureVersion { get; init; } = 1;

    public ArtifactReference? ArchitectureArtifact { get; init; }
}
