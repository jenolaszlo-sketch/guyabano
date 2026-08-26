using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public sealed record CiScaffoldResult(
    [property: JsonPropertyName("artifacts")]
    IReadOnlyList<string> Artifacts,
    [property: JsonPropertyName("removedFiles")]
    IReadOnlyList<string> RemovedFiles,
    [property: JsonPropertyName("operationCount")]
    int OperationCount);
