using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class DiscoveredDomainDefault
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    [JsonPropertyName("missingInformation")]
    public required string MissingInformation { get; init; }

    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    [JsonPropertyName("reasons")]
    public required List<string> Reasons { get; init; }

    [JsonPropertyName("impact")]
    public required string Impact { get; init; }

    [JsonPropertyName("affectedCapabilities")]
    public required List<string> AffectedCapabilities { get; init; }

    [JsonPropertyName("userOverridable")]
    public required bool UserOverridable { get; init; }
}
