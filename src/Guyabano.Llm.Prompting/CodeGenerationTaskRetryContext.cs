namespace Guyabano.Llm.Prompting;

public sealed record CodeGenerationTaskRetryContext(
    int PreviousAttempt,
    string PreviousModel,
    string Failure,
    string? Error,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> WrittenFiles);
