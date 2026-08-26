using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class TopologyModulePlan
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("boundedContextName")]
    public required string BoundedContextName { get; init; }

    [JsonPropertyName("projectName")]
    public required string ProjectName { get; init; }

    [JsonPropertyName("responsibilities")]
    public required List<string> Responsibilities { get; init; }
}
