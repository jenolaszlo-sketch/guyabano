using Guyabano.Llm.Prompting;

namespace Guyabano.Llm.CodeGeneration;

public interface ICodeGenerationTaskService
{
    Task<CodeGenerationOutcome> GenerateAndEmitAsync(
        CodeGenerationTaskContext task,
        string outputRoot,
        string model,
        int maxTokens = 8000,
        CancellationToken cancellationToken = default);
}
