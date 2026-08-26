using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class BoundedContextComponentManifest
{
    [JsonPropertyName("boundedContextName")]
    public required string BoundedContextName { get; init; }

    [JsonPropertyName("components")]
    public required List<StagedComponent> Components { get; init; }

    [JsonPropertyName("decisions")]
    public required List<StagedArchitectureDecision> Decisions { get; init; }

    [JsonPropertyName("inferredDefaults")]
    public required List<DiscoveredDomainDefault> InferredDefaults { get; init; }
}
