using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitecturePractice
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("guidance")]
    public required string Guidance { get; init; }

    [JsonPropertyName("applicability")]
    public required string Applicability { get; init; }

    [JsonPropertyName("reasons")]
    public required List<string> Reasons { get; init; }

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }
}
