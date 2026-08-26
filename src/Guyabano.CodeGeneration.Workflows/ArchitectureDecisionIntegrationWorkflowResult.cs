using Penghou.Baize;
using Guyabano.CodeGeneration.Planning;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record ArchitectureDecisionIntegrationWorkflowResult(
    int ArchitectureVersion,
    bool Succeeded,
    string Failure,
    string? Error,
    string Model,
    ArchitectureDecisionPatch? Patch,
    CodeGenerationPlan? IntegratedPlan,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts,
    CodeGenerationUsage? Usage = null,
    CodeGenerationDiagnostics? Diagnostics = null,
    string? FinishReason = null,
    ArtifactReference? Artifact = null);
