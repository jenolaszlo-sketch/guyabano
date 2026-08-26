namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationBuildCorrection(
    int PreviousAttempt,
    string PreviousModel,
    string Failure,
    string? Error,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> WrittenFiles);
