using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class PlannedContract
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    [SchemaDescription("Contract kind such as Interface, DTO, ValueObject, Options, or EndpointContract.")]
    public required string Kind { get; init; }

    [JsonPropertyName("moduleId")]
    public required string ModuleId { get; init; }

    [JsonPropertyName("purpose")]
    public required string Purpose { get; init; }

    [JsonPropertyName("members")]
    [SchemaDescription("Public method signatures or property shapes required by downstream tasks.")]
    public required List<string> Members { get; init; }
}
