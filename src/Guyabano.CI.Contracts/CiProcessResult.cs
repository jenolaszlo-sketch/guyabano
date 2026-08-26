using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

public sealed record CiProcessResult(
    [property: JsonPropertyName("command")]
    string Command,
    [property: JsonPropertyName("arguments")]
    IReadOnlyList<string> Arguments,
    [property: JsonPropertyName("workingDirectory")]
    string WorkingDirectory,
    [property: JsonPropertyName("exitCode")]
    int ExitCode,
    [property: JsonPropertyName("standardOutput")]
    string StandardOutput,
    [property: JsonPropertyName("standardError")]
    string StandardError)
{
    [JsonPropertyName("success")]
    public bool Success => ExitCode == 0;
}
