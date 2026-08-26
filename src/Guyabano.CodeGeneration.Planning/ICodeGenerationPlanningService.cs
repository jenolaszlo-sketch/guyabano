namespace Guyabano.CodeGeneration.Planning;

public interface ICodeGenerationPlanningService
{
    Task<CodeGenerationPlanningOutcome> PlanAsync(
        string request,
        string model,
        int maxTokens = 12000,
        string? previousFailure = null,
        CancellationToken cancellationToken = default);
}
