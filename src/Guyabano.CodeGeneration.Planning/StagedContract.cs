using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class StagedContract
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("moduleName")]
    public required string ModuleName { get; init; }

    [JsonPropertyName("purpose")]
    public required string Purpose { get; init; }

    [JsonPropertyName("members")]
    public required List<string> Members { get; init; }

    [JsonPropertyName("capabilityNames")]
    public required List<string> CapabilityNames { get; init; }
}
