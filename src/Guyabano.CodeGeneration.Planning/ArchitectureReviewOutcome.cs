using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning;

public sealed record ArchitectureReviewOutcome(
    bool Succeeded,
    PlanningFailure Failure,
    string? Error,
    string Model,
    ArchitectureReview? Review,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts)
{
    public LlmUsage? Usage { get; init; }
    public LlmProviderDiagnostics? Diagnostics { get; init; }
    public string? FinishReason { get; init; }
}
