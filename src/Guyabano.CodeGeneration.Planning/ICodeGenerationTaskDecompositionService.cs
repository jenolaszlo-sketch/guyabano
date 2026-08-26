namespace Guyabano.CodeGeneration.Planning;

public interface ICodeGenerationTaskDecompositionService
{
    Task<CodeGenerationDecompositionOutcome> DecomposeAsync(
        ComponentWorkContext workContext,
        string model,
        int maxTokens = 8000,
        CancellationToken cancellationToken = default);
}
