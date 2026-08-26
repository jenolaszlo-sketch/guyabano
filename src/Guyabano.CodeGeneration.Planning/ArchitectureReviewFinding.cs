using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureReviewFinding
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("severity")]
    public required ArchitectureReviewSeverity Severity { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("evidence")]
    public required List<string> Evidence { get; init; }

    [JsonPropertyName("affectedIds")]
    public required List<string> AffectedIds { get; init; }

    [JsonPropertyName("suggestedResolution")]
    public required string SuggestedResolution { get; init; }

    [JsonPropertyName("requiresUserInput")]
    public required bool RequiresUserInput { get; init; }
}
