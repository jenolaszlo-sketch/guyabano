using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Input for authoring a Fuwen workflow. Either a free user request or a
/// resolved execution plan (Phase 2): when <see cref="ExecutionPlan"/> is set,
/// the model translates the authoritative decomposition it renders rather
/// than rediscovering architecture from <see cref="Request"/> alone.
/// Patch-based authoring additionally supplies <see cref="PriorDsl"/> (the
/// current admitted workflow) with <see cref="ChangedSteps"/> (the step ids
/// this revision may modify); every other node must be reproduced verbatim.
/// </summary>
public sealed record WorkflowAuthoringPromptContext(
    string Request,
    string CatalogueSummary,
    int MaxTokens,
    string? PreviousFailure = null,
    string? ExecutionPlan = null,
    string? PriorDsl = null,
    IReadOnlyList<string>? ChangedSteps = null) : ILlmPromptContext
{
    public double Temperature { get; init; } = 0.2;
}
