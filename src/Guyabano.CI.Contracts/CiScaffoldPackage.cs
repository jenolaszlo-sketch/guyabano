using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public sealed record CiScaffoldPackage(
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("version")]
    string Version);
