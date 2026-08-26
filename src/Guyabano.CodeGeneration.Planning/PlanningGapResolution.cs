using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class PlanningGapResolution
{
    [JsonPropertyName("resolutionKind")]
    public required string ResolutionKind { get; init; }

    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    [JsonPropertyName("reasons")]
    public required List<string> Reasons { get; init; }

    [JsonPropertyName("alternativesConsidered")]
    public required List<string> AlternativesConsidered { get; init; }

    [JsonPropertyName("consequences")]
    public required List<string> Consequences { get; init; }

    [JsonPropertyName("userOverridable")]
    public required bool UserOverridable { get; init; }

    [JsonPropertyName("requiresUserInput")]
    public required bool RequiresUserInput { get; init; }

    [JsonPropertyName("userQuestion")]
    public required string UserQuestion { get; init; }
}
