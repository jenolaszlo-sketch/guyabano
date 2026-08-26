using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class CodeGenerationPlan
{
    [JsonPropertyName("mission")]
    public ProductMission Mission { get; init; } = new()
    {
        GuidingIntent = string.Empty,
        SuccessOutcomes = [],
        Constraints = [],
        NonGoals = []
    };

    [JsonPropertyName("title")]
    [SchemaDescription("Short name for the requested solution or change.")]
    public required string Title { get; init; }

    [JsonPropertyName("summary")]
    [SchemaDescription("Concise description of the planned solution and its boundaries.")]
    public required string Summary { get; init; }

    [JsonPropertyName("assumptions")]
    [SchemaDescription("Explicit assumptions needed to make the plan actionable; use an empty array when none are needed.")]
    public required List<string> Assumptions { get; init; }

    [JsonPropertyName("solution")]
    public required PlannedSolution Solution { get; init; }

    [JsonPropertyName("projects")]
    public required List<PlannedProject> Projects { get; init; }

    [JsonPropertyName("modules")]
    public required List<PlannedModule> Modules { get; init; }

    [JsonPropertyName("contracts")]
    public required List<PlannedContract> Contracts { get; init; }

    [JsonPropertyName("decisions")]
    public required List<ArchitectureDecision> Decisions { get; init; }

    [JsonPropertyName("architectureNotes")]
    [SchemaDescription("Traceable defaults and domain constraints inferred where the request was silent; use an empty array when no inference was needed.")]
    public required List<ArchitectureNote> ArchitectureNotes { get; init; }

    [JsonPropertyName("useCases")]
    public List<PlanUseCase> UseCases { get; init; } = [];

    [JsonPropertyName("acceptanceCriteria")]
    public required List<PlanAcceptanceCriterion> AcceptanceCriteria { get; init; }

    [JsonPropertyName("tasks")]
    public required List<GenerationTaskPlan> Tasks { get; init; }
}
