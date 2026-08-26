using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class DiscoveredProductAmbiguity
{
    [JsonPropertyName("question")]
    public required string Question { get; init; }

    [JsonPropertyName("whyItMatters")]
    public required string WhyItMatters { get; init; }

    [JsonPropertyName("affectedCapabilities")]
    public required List<string> AffectedCapabilities { get; init; }
}
