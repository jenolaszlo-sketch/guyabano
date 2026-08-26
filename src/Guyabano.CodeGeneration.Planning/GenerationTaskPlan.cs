using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class GenerationTaskPlan
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("objective")]
    public required string Objective { get; init; }

    [JsonPropertyName("executionKind")]
    [SchemaDescription("Scaffolding uses deterministic tooling; CodeGeneration uses an LLM implementation activity.")]
    public required PlanTaskExecutionKind ExecutionKind { get; init; }

    [JsonPropertyName("moduleId")]
    [SchemaDescription("Required for CodeGeneration tasks and omitted for solution-level Scaffolding tasks.")]
    public string? ModuleId { get; init; }

    [JsonPropertyName("boundedContext")]
    [SchemaDescription("Domain boundary owning the task; omitted for solution-level scaffolding.")]
    public string? BoundedContext { get; init; }

    [JsonPropertyName("complexityPoints")]
    [SchemaDescription("Relative complexity using exactly one Fibonacci value: 1, 2, 3, 5, 8, or 13.")]
    public required int ComplexityPoints { get; init; }

    [JsonPropertyName("complexityReasons")]
    public required List<string> ComplexityReasons { get; init; }

    [JsonPropertyName("decompositionRecommended")]
    [SchemaDescription("True when the task should receive another planning pass before implementation.")]
    public required bool DecompositionRecommended { get; init; }

    [JsonPropertyName("estimatedFiles")]
    public required int EstimatedFiles { get; init; }

    [JsonPropertyName("dependsOn")]
    [SchemaDescription("IDs of tasks that must complete first.")]
    public required List<string> DependsOn { get; init; }

    [JsonPropertyName("contractIds")]
    public required List<string> ContractIds { get; init; }

    [JsonPropertyName("relationships")]
    [SchemaDescription("Typed contract and component relationships preserved from component design.")]
    public required ComponentRelationshipPlan Relationships { get; init; }

    [JsonPropertyName("decisionIds")]
    [SchemaDescription("Architecture decision IDs that constrain this task.")]
    public required List<string> DecisionIds { get; init; }

    [JsonPropertyName("acceptanceCriterionIds")]
    public required List<string> AcceptanceCriterionIds { get; init; }

    [JsonPropertyName("deliverables")]
    public required List<string> Deliverables { get; init; }

    [JsonPropertyName("verificationKinds")]
    public required List<string> VerificationKinds { get; init; }
}
