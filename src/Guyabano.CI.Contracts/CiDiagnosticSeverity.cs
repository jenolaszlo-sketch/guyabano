using System.Text.Json.Serialization;

namespace Guyabano.CI.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CiDiagnosticSeverity
{
    Warning,
    Error
}
