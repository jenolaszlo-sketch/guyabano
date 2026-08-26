namespace Guyabano.Llm.CodeGeneration;

internal interface ICodeEmitter
{
    Task<CodeEmitResult> EmitAsync(
        CodeGenerationResult result,
        string outputRoot,
        CancellationToken cancellationToken = default);
}
