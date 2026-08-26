using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public sealed record CiStreamEvent(
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("phase")]
    string? Phase = null,
    [property: JsonPropertyName("stream")]
    string? Stream = null,
    [property: JsonPropertyName("message")]
    string? Message = null,
    [property: JsonPropertyName("data")]
    object? Data = null,
    [property: JsonPropertyName("success")]
    bool? Success = null,
    [property: JsonPropertyName("exitCode")]
    int? ExitCode = null,
    [property: JsonPropertyName("diagnostic")]
    CiDiagnostic? Diagnostic = null);
