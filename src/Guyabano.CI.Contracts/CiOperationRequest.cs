using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public abstract record CiOperationRequest(
    [property: JsonPropertyName("relativePath")]
    string RelativePath,

    [property: JsonPropertyName("projectOrSolutionFile")]
    string? ProjectOrSolutionFile = null);
