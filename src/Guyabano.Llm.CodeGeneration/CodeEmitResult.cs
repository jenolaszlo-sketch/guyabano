namespace Guyabano.Llm.CodeGeneration;

internal sealed record CodeEmitResult(
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> SkippedFiles);
