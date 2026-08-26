using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class DomainCapability
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("businessRules")]
    public required List<string> BusinessRules { get; init; }

}
