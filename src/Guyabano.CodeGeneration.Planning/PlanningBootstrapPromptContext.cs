using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Input for proposing a planning stage plan.</summary>
public sealed record PlanningBootstrapPromptContext(
    string Goal,
    string StageCatalogue,
    string CatalogueVersion,
    IReadOnlyList<string> CurrentRevisions,
    int MaxTokens,
    string? PreviousFailure = null) : ILlmPromptContext
{
    public double Temperature { get; init; } = 0.1;
}
