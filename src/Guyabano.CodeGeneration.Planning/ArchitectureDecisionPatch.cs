using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureDecisionPatch
{
    [JsonPropertyName("appliedResolutionIds")]
    public required List<string> AppliedResolutionIds { get; init; }

    [JsonPropertyName("assumptionsToAdd")]
    public required List<string> AssumptionsToAdd { get; init; }

    [JsonPropertyName("projectReplacements")]
    public required List<PlannedProject> ProjectReplacements { get; init; }

    [JsonPropertyName("moduleReplacements")]
    public required List<PlannedModule> ModuleReplacements { get; init; }

    [JsonPropertyName("contractReplacements")]
    public required List<PlannedContract> ContractReplacements { get; init; }

    [JsonPropertyName("contractAdditions")]
    public required List<PlannedContract> ContractAdditions { get; init; }

    [JsonPropertyName("decisionReplacements")]
    public required List<ArchitectureDecision> DecisionReplacements { get; init; }

    [JsonPropertyName("decisionAdditions")]
    public required List<ArchitectureDecision> DecisionAdditions { get; init; }

    [JsonPropertyName("architectureNoteReplacements")]
    public required List<ArchitectureNote> ArchitectureNoteReplacements
    { get; init; }

    [JsonPropertyName("architectureNoteAdditions")]
    public required List<ArchitectureNote> ArchitectureNoteAdditions
    { get; init; }

    [JsonPropertyName("acceptanceCriterionReplacements")]
    public required List<PlanAcceptanceCriterion>
        AcceptanceCriterionReplacements
    { get; init; }

    [JsonPropertyName("acceptanceCriterionAdditions")]
    public required List<PlanAcceptanceCriterion>
        AcceptanceCriterionAdditions
    { get; init; }

    [JsonPropertyName("taskReplacements")]
    public required List<GenerationTaskPlan> TaskReplacements { get; init; }
}
