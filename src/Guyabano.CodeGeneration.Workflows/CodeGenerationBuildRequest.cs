namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationBuildRequest(
    IReadOnlyList<string> WrittenFiles,
    string ProjectOrSolutionFile,
    int BuildAttempt = 1,
    int MaximumBuildAttempts = 1);
