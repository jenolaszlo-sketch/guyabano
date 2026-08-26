using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class PlannedSolution
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("path")]
    [SchemaDescription("Relative solution path including the .sln or .slnx file name.")]
    public required string Path { get; init; }
}
