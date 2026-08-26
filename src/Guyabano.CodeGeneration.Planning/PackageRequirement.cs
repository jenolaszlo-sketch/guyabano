using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class PackageRequirement
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    [SchemaDescription("Exact or centrally managed package version when known; otherwise an empty string.")]
    public required string Version { get; init; }

    [JsonPropertyName("purpose")]
    public required string Purpose { get; init; }
}
