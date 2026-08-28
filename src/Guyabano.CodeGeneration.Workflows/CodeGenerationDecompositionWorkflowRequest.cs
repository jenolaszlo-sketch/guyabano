using Guyabano.CodeGeneration.Planning;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationDecompositionWorkflowRequest(
    CodeGenerationPlan Plan,
    string ParentTaskId,
    IReadOnlyList<ArtifactReference> UpstreamDecompositionArtifacts,
    int ArchitectureVersion = 1,
    ArtifactReference? ArchitectureArtifact = null)
{
    public RepositoryContextReference? RepositoryContext { get; init; }
}
