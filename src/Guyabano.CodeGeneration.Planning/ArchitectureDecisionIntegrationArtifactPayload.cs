namespace Guyabano.CodeGeneration.Planning;

public sealed record ArchitectureDecisionIntegrationArtifactPayload(
    int ArchitectureVersion,
    ArchitectureDecisionPatch Patch,
    CodeGenerationPlan IntegratedPlan);
