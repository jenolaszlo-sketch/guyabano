using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Input for proposing a <see cref="WorkflowPatch"/>: the goal, the rendered
/// current design with its base fingerprint, the changed artifact revisions
/// that motivate the delta, and the catalogue available for new bindings.
/// </summary>
public sealed record WorkflowPatchPromptContext(
    string Goal,
    string ExecutionPlan,
    string BaseDesignFingerprint,
    IReadOnlyList<string> ChangedArtifacts,
    string CatalogueSummary,
    int MaxTokens,
    string? PreviousFailure = null) : ILlmPromptContext
{
    public double Temperature { get; init; } = 0.1;
}
