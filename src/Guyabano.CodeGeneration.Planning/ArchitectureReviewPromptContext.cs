using Guyabano.Llm.Prompting;
using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning;

public sealed record ArchitectureReviewPromptContext(
    CodeGenerationPlan Plan,
    int ReviewPass,
    LlmResponseFormat ResponseFormat,
    int MaxTokens = 8000,
    double Temperature = 0.1,
    ArchitectureReview? PreviousReview = null) : ILlmPromptContext
{
    public string? PreviousFailure { get; init; }
}
