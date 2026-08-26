using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class PlannedProject
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("path")]
    [SchemaDescription("Relative project path including the project file name.")]
    public required string Path { get; init; }

    [JsonPropertyName("kind")]
    [SchemaDescription("Scaffolding kind such as WebApi, Library, UnitTests, or IntegrationTests.")]
    public required string Kind { get; init; }

    [JsonPropertyName("role")]
    [SchemaDescription("Architectural responsibility independent of the scaffolding kind.")]
    public required ProjectRole Role { get; init; }

    [JsonPropertyName("targetFramework")]
    [SchemaDescription("Target framework moniker such as net10.0.")]
    public required string TargetFramework { get; init; }

    [JsonPropertyName("responsibilities")]
    public required List<string> Responsibilities { get; init; }

    [JsonPropertyName("projectDependencies")]
    [SchemaDescription("Names of projects referenced by this project.")]
    public required List<string> ProjectDependencies { get; init; }

    [JsonPropertyName("packages")]
    [SchemaDescription("Package references consumed directly by this project; use an empty array when none are needed.")]
    public required List<PackageRequirement> Packages { get; init; }
}
