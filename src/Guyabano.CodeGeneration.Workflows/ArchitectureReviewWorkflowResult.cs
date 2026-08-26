using Penghou.Baize;
using Guyabano.CodeGeneration.Planning;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record ArchitectureReviewWorkflowResult(
    int ReviewPass,
    bool Succeeded,
    string Failure,
    string? Error,
    string Model,
    ArchitectureReview? Review,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts,
    CodeGenerationUsage? Usage = null,
    CodeGenerationDiagnostics? Diagnostics = null,
    string? FinishReason = null,
    ArtifactReference? Artifact = null);
