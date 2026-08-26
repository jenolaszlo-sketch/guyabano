using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureNote
{
    [JsonPropertyName("id")]
    [SchemaDescription("Stable unique ID for this recorded inference or default.")]
    public required string Id { get; init; }

    [JsonPropertyName("category")]
    public required ArchitectureNoteCategory Category { get; init; }

    [JsonPropertyName("subject")]
    [SchemaDescription("Short name of the behavior, constraint, or technical detail being resolved.")]
    public required string Subject { get; init; }

    [JsonPropertyName("missingInformation")]
    [SchemaDescription("What the request did not specify and why a decision was necessary.")]
    public required string MissingInformation { get; init; }

    [JsonPropertyName("decision")]
    [SchemaDescription("The concrete default, constraint, or technical choice selected by the architect.")]
    public required string Decision { get; init; }

    [JsonPropertyName("reasons")]
    [SchemaDescription("Concise evidence supporting the selected choice.")]
    public required List<string> Reasons { get; init; }

    [JsonPropertyName("impact")]
    [SchemaDescription("Observable or implementation impact of the choice, including when there is no external impact.")]
    public required string Impact { get; init; }

    [JsonPropertyName("affectedIds")]
    [SchemaDescription("Existing module, contract, decision, acceptance-criterion, task, project, or solution IDs constrained by this note.")]
    public required List<string> AffectedIds { get; init; }

    [JsonPropertyName("userOverridable")]
    [SchemaDescription("True when a later explicit user requirement may replace this inferred choice.")]
    public required bool UserOverridable { get; init; }
}
