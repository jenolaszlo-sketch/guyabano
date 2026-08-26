namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationUsage(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? PromptCacheHitTokens,
    int? PromptCacheMissTokens);
