using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class BoundedContextContractCatalog
{
    [JsonPropertyName("boundedContextName")]
    public required string BoundedContextName { get; init; }

    [JsonPropertyName("contracts")]
    public required List<StagedContract> Contracts { get; init; }

    [JsonPropertyName("decisions")]
    public required List<StagedArchitectureDecision> Decisions { get; init; }

    [JsonPropertyName("inferredDefaults")]
    public required List<DiscoveredDomainDefault> InferredDefaults { get; init; }
}
