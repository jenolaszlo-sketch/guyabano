namespace Guyabano.CodeGeneration.Planning;

public interface IArchitectureGapResolutionService
{
    Task<ArchitectureGapResolutionOutcome> ResolveAsync(
        CodeGenerationPlan plan,
        ArchitectureReviewFinding finding,
        IReadOnlyList<ArchitecturePractice> practices,
        int architectureVersion,
        string model,
        int maxTokens,
        string? previousFailure = null,
        CancellationToken cancellationToken = default);
}
