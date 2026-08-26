using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class DiscoveredUseCase
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("capabilityName")]
    public required string CapabilityName { get; init; }

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

    [JsonPropertyName("acceptanceCriteria")]
    public required List<DiscoveredAcceptanceCriterion> AcceptanceCriteria
    { get; init; }
}
