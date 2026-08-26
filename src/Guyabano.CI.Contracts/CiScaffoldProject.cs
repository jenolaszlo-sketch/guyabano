using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public sealed record CiScaffoldProject(
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("path")]
    string Path,
    [property: JsonPropertyName("kind")]
    string Kind,
    [property: JsonPropertyName("targetFramework")]
    string TargetFramework,
    [property: JsonPropertyName("projectDependencies")]
    IReadOnlyList<string> ProjectDependencies,
    [property: JsonPropertyName("packages")]
    IReadOnlyList<CiScaffoldPackage> Packages);
