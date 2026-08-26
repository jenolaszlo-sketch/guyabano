using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public sealed record CiDiagnostic(
    [property: JsonPropertyName("tool")]
    string Tool,
    [property: JsonPropertyName("code")]
    string Code,
    [property: JsonPropertyName("severity")]
    CiDiagnosticSeverity Severity,
    [property: JsonPropertyName("message")]
    string Message,
    [property: JsonPropertyName("filePath")]
    string? FilePath = null,
    [property: JsonPropertyName("projectPath")]
    string? ProjectPath = null,
    [property: JsonPropertyName("line")]
    int? Line = null,
    [property: JsonPropertyName("column")]
    int? Column = null);
