using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning;

public sealed record CodeGenerationDecompositionOutcome(
    bool Succeeded,
    PlanningFailure Failure,
    string? Error,
    string Model,
    CodeGenerationTaskDecomposition? Decomposition,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts)
{
    public LlmUsage? Usage { get; init; }

    public LlmProviderDiagnostics? Diagnostics { get; init; }

    public string? FinishReason { get; init; }
}
