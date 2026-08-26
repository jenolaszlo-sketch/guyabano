using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.Llm.CodeGeneration;

public sealed class CodeGenerationResult
{
    [JsonPropertyName("files"), SchemaDescription("The generated files, one entry per file.")]
    public required List<GeneratedFile> Files { get; init; }

    [JsonPropertyName("notes"), SchemaDescription("Brief explanation of what was generated.")]
    public string? Notes { get; init; }
}

public sealed class GeneratedFile
{
    [JsonPropertyName("path"), SchemaDescription("Relative file path, forward slashes, no leading slash, no ..")]
    public required string Path { get; init; }

    [JsonPropertyName("content"), SchemaDescription("Complete final content of the file.")]
    public required string Content { get; init; }
}