using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public sealed record CiScaffoldSolution(
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("path")]
    string Path);
