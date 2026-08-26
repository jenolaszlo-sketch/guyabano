using Penghou.Baize;
using Guyabano.CodeGeneration.Planning;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationWorkflowResult(
    bool Succeeded,
    string Failure,
    string? Error,
    string Model,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts,
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> SkippedFiles,
    CodeGenerationUsage? Usage = null,
    CodeGenerationDiagnostics? Diagnostics = null)
{
    public CodeGenerationBuildResult? Build { get; init; }

    public IReadOnlyList<CodeGenerationBuildResult> BuildAttempts
    {
        get;
        init;
    } = [];

    public IReadOnlyList<CodeGenerationTaskWorkflowResult> BuildRepairs
    {
        get;
        init;
    } = [];

    public string? FinishReason { get; init; }

    public CodeGenerationPlan? Plan { get; init; }

    public CodeGenerationScaffoldingResult? Scaffolding { get; init; }

    public IReadOnlyList<CodeGenerationTaskWorkflowResult> TaskResults
    {
        get;
        init;
    } = [];

    public IReadOnlyList<CodeGenerationDecompositionWorkflowResult>
        Decompositions
    { get; init; } = [];

    public int ArchitectureVersion { get; init; } = 1;

    public ArtifactReference? ArchitectureArtifact { get; init; }

    public IReadOnlyList<ArtifactReference> PlanningArtifacts { get; init; } = [];

    public IReadOnlyList<ArchitectureReviewWorkflowResult>
        ArchitectureReviews
    { get; init; } = [];

    public IReadOnlyList<ArchitectureDecisionIntegrationWorkflowResult>
        ArchitectureDecisionIntegrations
    { get; init; } = [];

    public IReadOnlyList<ArchitectureGapResolutionWorkflowResult>
        ArchitectureResolutions
    { get; init; } = [];

    public IReadOnlyList<ArchitecturePractice> ArchitecturePractices
    { get; init; } = [];

    public CodeGenerationContinuationInfo? Continuation { get; init; }
}
