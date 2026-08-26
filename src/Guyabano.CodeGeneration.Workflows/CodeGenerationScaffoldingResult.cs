namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationScaffoldingResult(
    bool Succeeded,
    string? Error,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<string> RemovedFiles,
    int CompletedOperations);
