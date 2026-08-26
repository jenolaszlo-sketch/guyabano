namespace Guyabano.CodeGeneration.Planning;

public interface IArchitectureDecisionIntegrator
{
    Task<ArchitectureDecisionIntegrationOutcome> IntegrateAsync(
        CodeGenerationPlan plan,
        ArchitectureReview resolvedReview,
        IReadOnlyList<ArchitectureGapResolution> resolvedDecisions,
        string model,
        int maxTokens,
        string? previousFailure = null,
        CancellationToken cancellationToken = default);
}
