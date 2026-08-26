namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationBuildResult(
    bool Succeeded,
    int? ExitCode,
    string? Error,
    IReadOnlyList<CodeGenerationBuildDiagnostic> Diagnostics);
