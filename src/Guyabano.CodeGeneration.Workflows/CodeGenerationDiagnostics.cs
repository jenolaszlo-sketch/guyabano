namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationDiagnostics(
    string Provider,
    string? ActualModel,
    string? Api,
    bool? Done,
    string? DoneReason,
    double? TotalDurationMilliseconds,
    double? LoadDurationMilliseconds,
    double? PromptEvaluationDurationMilliseconds,
    double? GenerationDurationMilliseconds,
    double? GenerationTokensPerSecond,
    int? NativeToolCallCount,
    int? ContentLength);
