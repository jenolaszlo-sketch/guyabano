using System.Text.Json.Serialization;

namespace Guyabano.Messaging;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowDiagnosticSeverity
{
    Information,
    Warning,
    Error
}
