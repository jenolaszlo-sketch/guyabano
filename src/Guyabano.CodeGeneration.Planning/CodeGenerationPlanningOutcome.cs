using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning;

public sealed record CodeGenerationPlanningOutcome(
    bool Succeeded,
    PlanningFailure Failure,
    string? Error,
    string Model,
    CodeGenerationPlan? Plan,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts)
{
    public LlmUsage? Usage { get; init; }

    public LlmProviderDiagnostics? Diagnostics { get; init; }

    public string? FinishReason { get; init; }

    public StagedPlanningArtifacts? StagedArtifacts { get; init; }
}
