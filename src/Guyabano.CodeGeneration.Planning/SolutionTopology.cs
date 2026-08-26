using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class SolutionTopology
{
    [JsonPropertyName("solution")]
    public required PlannedSolution Solution { get; init; }

    [JsonPropertyName("projects")]
    public required List<PlannedProject> Projects { get; init; }

    [JsonPropertyName("boundedContexts")]
    public required List<BoundedContextPlan> BoundedContexts { get; init; }

    [JsonPropertyName("modules")]
    public required List<TopologyModulePlan> Modules { get; init; }

    [JsonPropertyName("decisions")]
    public required List<StagedArchitectureDecision> Decisions { get; init; }
}
