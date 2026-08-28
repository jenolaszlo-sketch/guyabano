using Guyabano.CodeGeneration.Planning;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record ArchitectureDecisionIntegrationWorkflowRequest(
    CodeGenerationPlan Plan,
    ArchitectureReview ResolvedReview,
    IReadOnlyList<ArchitectureGapResolution> ResolvedDecisions,
    int ArchitectureVersion,
    ArtifactReference? PreviousArchitectureArtifact = null,
    IReadOnlyList<ArtifactReference>? ResolutionArtifacts = null)
{
    public RepositoryContextReference? RepositoryContext { get; init; }
}
