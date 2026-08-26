using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureGapResolution
{
    [JsonPropertyName("findingId")]
    public required string FindingId { get; init; }

    [JsonPropertyName("resolutionKind")]
    public required string ResolutionKind { get; init; }

    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    [JsonPropertyName("decisionRecord")]
    public required ArchitectureDecision DecisionRecord { get; init; }

    [JsonPropertyName("appliedPractice")]
    public required ArchitecturePractice AppliedPractice { get; init; }

    [JsonPropertyName("reusedExistingPractice")]
    public required bool ReusedExistingPractice { get; init; }

    [JsonPropertyName("reasons")]
    public required List<string> Reasons { get; init; }

    [JsonPropertyName("alternativesConsidered")]
    public required List<string> AlternativesConsidered { get; init; }

    [JsonPropertyName("consequences")]
    public required List<string> Consequences { get; init; }

    [JsonPropertyName("affectedIds")]
    public required List<string> AffectedIds { get; init; }

    [JsonPropertyName("userOverridable")]
    public required bool UserOverridable { get; init; }

    [JsonPropertyName("requiresUserInput")]
    public required bool RequiresUserInput { get; init; }

    [JsonPropertyName("userQuestion")]
    public required string UserQuestion { get; init; }
}
