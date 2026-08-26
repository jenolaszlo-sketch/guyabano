using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning;

public sealed record ArchitectureGapResolutionOutcome(
    bool Succeeded,
    PlanningFailure Failure,
    string? Error,
    string Model,
    ArchitectureGapResolution? Resolution,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts)
{
    public LlmUsage? Usage { get; init; }
    public LlmProviderDiagnostics? Diagnostics { get; init; }
    public string? FinishReason { get; init; }
}
