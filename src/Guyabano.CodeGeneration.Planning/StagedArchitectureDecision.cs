using System.Text.Json.Serialization;
using Penghou.Baize.Tools.Schema;

namespace Guyabano.CodeGeneration.Planning;

public sealed class StagedArchitectureDecision
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    [JsonPropertyName("reasons")]
    public required List<string> Reasons { get; init; }

    [JsonPropertyName("alternativesRejected")]
    public required List<string> AlternativesRejected { get; init; }

    [JsonPropertyName("relatedPackages")]
    [SchemaDescription("NuGet package IDs directly involved in this decision. Never include project, assembly, module, or namespace names. Every ID must already be declared by the owning project; use an empty array when no NuGet package is involved.")]
    public required List<string> RelatedPackages { get; init; }

    [JsonPropertyName("affectedContextNames")]
    public required List<string> AffectedContextNames { get; init; }
}
