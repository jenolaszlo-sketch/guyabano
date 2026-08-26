using Penghou.Baize;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Planning;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record ArchitectureGapResolutionWorkflowResult(
    bool Succeeded,
    string Failure,
    string? Error,
    string Model,
    ArchitectureGapResolution? Resolution,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts,
    CodeGenerationUsage? Usage,
    CodeGenerationDiagnostics? Diagnostics,
    string? FinishReason,
    ArtifactReference? Artifact);
