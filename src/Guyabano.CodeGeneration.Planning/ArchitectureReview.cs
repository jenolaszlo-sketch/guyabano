using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureReview
{
    [JsonPropertyName("approved")]
    [SchemaDescription("True only when there are no blocking findings.")]
    public required bool Approved { get; init; }

    [JsonPropertyName("findings")]
    public required List<ArchitectureReviewFinding> Findings { get; init; }
}
