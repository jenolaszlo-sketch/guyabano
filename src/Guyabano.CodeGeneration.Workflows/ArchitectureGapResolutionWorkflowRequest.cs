using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Planning;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record ArchitectureGapResolutionWorkflowRequest(
    CodeGenerationPlan Plan,
    ArchitectureReviewFinding Finding,
    int ArchitectureVersion,
    ArtifactReference? ArchitectureArtifact,
    IReadOnlyList<ArtifactReference> PlanningArtifacts,
    IReadOnlyList<ArchitecturePractice>? ArchitecturePractices = null)
{
    public RepositoryContextReference? RepositoryContext { get; init; }
}
