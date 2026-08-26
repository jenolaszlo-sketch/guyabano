using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class DecomposedArtifactPlan
{
    [JsonPropertyName("path")]
    [SchemaDescription("Exact solution-relative file path owned by this leaf task.")]
    public required string Path { get; init; }

    [JsonPropertyName("kind")]
    [SchemaDescription("Artifact kind such as CSharpClass, CSharpInterface, CSharpDto, CSharpHost, Test, JSON, or XML.")]
    public required string Kind { get; init; }

    [JsonPropertyName("namespace")]
    [SchemaDescription("Exact namespace for a source artifact, or an empty string when not applicable.")]
    public required string Namespace { get; init; }

    [JsonPropertyName("typeNames")]
    [SchemaDescription("Exact declared type names; use an empty array for top-level or non-source artifacts.")]
    public required List<string> TypeNames { get; init; }

    [JsonPropertyName("requirements")]
    [SchemaDescription("Concrete behavior and structure required in this artifact without implementation code.")]
    public required List<string> Requirements { get; init; }
}
