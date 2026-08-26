namespace Guyabano.CodeGeneration.Planning;

public interface IArchitectureReviewService
{
    Task<ArchitectureReviewOutcome> ReviewAsync(
        CodeGenerationPlan plan,
        int reviewPass,
        string model,
        int maxTokens,
        ArchitectureReview? previousReview = null,
        string? previousFailure = null,
        CancellationToken cancellationToken = default);
}
