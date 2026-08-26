using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class TaskArchitectureGap
{
    [JsonPropertyName("question")]
    public required string Question { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("affectedContractIds")]
    public required List<string> AffectedContractIds { get; init; }

    [JsonPropertyName("affectedDecisionIds")]
    public required List<string> AffectedDecisionIds { get; init; }
}
