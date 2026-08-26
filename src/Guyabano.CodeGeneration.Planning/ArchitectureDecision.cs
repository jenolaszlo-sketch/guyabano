using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureDecision
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    [JsonPropertyName("reasons")]
    public required List<string> Reasons { get; init; }

    [JsonPropertyName("alternativesRejected")]
    public required List<string> AlternativesRejected { get; init; }

    [JsonPropertyName("relatedPackages")]
    [SchemaDescription("NuGet package IDs directly involved in this decision. Never include project, assembly, module, or namespace names. Every ID must already be declared in a project packages array; use an empty array when no NuGet package is involved.")]
    public required List<string> RelatedPackages { get; init; }
}
