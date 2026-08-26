using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ProductMission
{
    [JsonPropertyName("guidingIntent")]
    public required string GuidingIntent { get; init; }

    [JsonPropertyName("successOutcomes")]
    public required List<string> SuccessOutcomes { get; init; }

    [JsonPropertyName("constraints")]
    public required List<string> Constraints { get; init; }

    [JsonPropertyName("nonGoals")]
    public required List<string> NonGoals { get; init; }
}
