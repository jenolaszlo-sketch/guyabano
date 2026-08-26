using Guyabano.Llm.Prompting;
using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning;

public sealed record CodeGenerationPlanningPromptContext(
    string Request,
    LlmResponseFormat ResponseFormat,
    int MaxTokens = 12000,
    double Temperature = 0.1) : ILlmPromptContext
{
    public string? PreviousFailure { get; init; }
}
