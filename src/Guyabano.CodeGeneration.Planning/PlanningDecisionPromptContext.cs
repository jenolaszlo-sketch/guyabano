using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Input for proposing one planning loop decision.</summary>
public sealed record PlanningDecisionPromptContext(
    string Goal,
    string ExecutionPlan,
    string DesignFingerprint,
    string WorkflowVersion,
    bool WorkflowComplete,
    string EvidenceSummary,
    IReadOnlyList<string> FreshArtifacts,
    int IterationsRemaining,
    int MutationsRemaining,
    int StructuralRemaining,
    int ModelCallsRemaining,
    int? TokensRemaining,
    int MaxTokens,
    string? PreviousFailure = null) : ILlmPromptContext
{
    public double Temperature { get; init; } = 0.1;
}
