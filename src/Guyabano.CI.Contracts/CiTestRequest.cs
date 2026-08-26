using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public sealed record CiTestRequest(
    string RelativePath,
    string? ProjectOrSolutionFile = null,
    [property: JsonPropertyName("noBuild")]
    bool NoBuild = false,
    [property: JsonPropertyName("noRestore")]
    bool NoRestore = false)
    : CiOperationRequest(RelativePath, ProjectOrSolutionFile);
