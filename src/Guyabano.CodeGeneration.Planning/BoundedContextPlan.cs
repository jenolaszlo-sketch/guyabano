using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class BoundedContextPlan
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("purpose")]
    public required string Purpose { get; init; }

    [JsonPropertyName("capabilityNames")]
    public required List<string> CapabilityNames { get; init; }

    [JsonPropertyName("dependsOnContextNames")]
    public required List<string> DependsOnContextNames { get; init; }

    [JsonPropertyName("inboundAdapters")]
    public required List<string> InboundAdapters { get; init; }

    [JsonPropertyName("outboundAdapters")]
    public required List<string> OutboundAdapters { get; init; }
}
