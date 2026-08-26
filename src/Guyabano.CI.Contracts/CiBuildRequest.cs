using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public sealed record CiBuildRequest(
    string RelativePath,
    string? ProjectOrSolutionFile = null,
    [property: JsonPropertyName("noRestore")]
    bool NoRestore = false)
    : CiOperationRequest(RelativePath, ProjectOrSolutionFile);
