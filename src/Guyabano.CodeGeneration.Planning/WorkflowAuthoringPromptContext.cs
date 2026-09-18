using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Input for authoring a Fuwen workflow from a user request.</summary>
public sealed record WorkflowAuthoringPromptContext(
    string Request,
    string CatalogueSummary,
    int MaxTokens,
    string? PreviousFailure = null) : ILlmPromptContext
{
    public double Temperature { get; init; } = 0.2;
}
