using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class DomainTerm
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("definition")]
    public required string Definition { get; init; }
}
