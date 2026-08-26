using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class PlannedModule
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("boundedContext")]
    public string BoundedContext { get; init; } = string.Empty;

    [JsonPropertyName("projectName")]
    public required string ProjectName { get; init; }

    [JsonPropertyName("responsibilities")]
    public required List<string> Responsibilities { get; init; }
}
