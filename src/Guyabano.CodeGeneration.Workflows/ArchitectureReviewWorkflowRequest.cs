using Guyabano.CodeGeneration.Planning;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record ArchitectureReviewWorkflowRequest(
    CodeGenerationPlan Plan,
    int ReviewPass,
    ArchitectureReview? PreviousReview = null,
    int ArchitectureVersion = 1,
    ArtifactReference? PreviousArchitectureArtifact = null,
    IReadOnlyList<ArtifactReference>? PlanningArtifacts = null)
{
    public RepositoryContextReference? RepositoryContext { get; init; }
}
