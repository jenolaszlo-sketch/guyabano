using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class DomainDiscovery
{
    [JsonPropertyName("mission")]
    public required ProductMission Mission { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("terms")]
    public required List<DomainTerm> Terms { get; init; }

    [JsonPropertyName("capabilities")]
    public required List<DomainCapability> Capabilities { get; init; }

    [JsonPropertyName("useCases")]
    public required List<DiscoveredUseCase> UseCases { get; init; }

    [JsonPropertyName("qualityAttributes")]
    public required List<string> QualityAttributes { get; init; }

    [JsonPropertyName("assumptions")]
    public required List<string> Assumptions { get; init; }

    [JsonPropertyName("inferredDefaults")]
    public required List<DiscoveredDomainDefault> InferredDefaults { get; init; }

    [JsonPropertyName("productAmbiguities")]
    public required List<DiscoveredProductAmbiguity> ProductAmbiguities
    { get; init; }
}
