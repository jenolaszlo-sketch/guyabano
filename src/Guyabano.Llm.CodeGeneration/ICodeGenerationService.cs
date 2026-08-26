namespace Guyabano.Llm.CodeGeneration;

public interface ICodeGenerationService
{
    Task<CodeGenerationOutcome> GenerateAndEmitAsync(
        string task,
        string outputRoot,
        string model,
        string projectName,
        string? rootNamespace = null,
        string targetFramework = "net10.0",
        int maxTokens = 8000,
        CancellationToken cancellationToken = default);
}
