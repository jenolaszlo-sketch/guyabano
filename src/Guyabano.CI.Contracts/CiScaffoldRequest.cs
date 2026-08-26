using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public sealed record CiScaffoldRequest(
    string RelativePath,
    [property: JsonPropertyName("solution")]
    CiScaffoldSolution Solution,
    [property: JsonPropertyName("projects")]
    IReadOnlyList<CiScaffoldProject> Projects)
    : CiOperationRequest(RelativePath);
