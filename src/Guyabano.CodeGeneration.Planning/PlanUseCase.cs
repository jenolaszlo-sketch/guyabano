using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class PlanUseCase
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("capability")]
    public required string Capability { get; init; }

    [JsonPropertyName("boundedContext")]
    public required string BoundedContext { get; init; }

    [JsonPropertyName("actor")]
    public required string Actor { get; init; }

    [JsonPropertyName("objective")]
    public required string Objective { get; init; }

    [JsonPropertyName("preconditions")]
    public required List<string> Preconditions { get; init; }

    [JsonPropertyName("inputs")]
    public required List<string> Inputs { get; init; }

    [JsonPropertyName("businessRules")]
    public required List<string> BusinessRules { get; init; }

    [JsonPropertyName("outcomes")]
    public required List<string> Outcomes { get; init; }

    [JsonPropertyName("errorOutcomes")]
    public required List<string> ErrorOutcomes { get; init; }

    [JsonPropertyName("acceptanceCriterionIds")]
    public required List<string> AcceptanceCriterionIds { get; init; }
}
