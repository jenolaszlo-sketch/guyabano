namespace Guyabano.CodeGeneration.Planning;

public sealed record ArchitecturePlanArtifactPayload(
    int ArchitectureVersion,
    CodeGenerationPlan Plan,
    ArchitectureReview Review);
