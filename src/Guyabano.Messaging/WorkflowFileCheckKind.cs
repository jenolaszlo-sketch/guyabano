using System.Text.Json.Serialization;

namespace Guyabano.Messaging;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowFileCheckKind
{
    Syntax,
    Compilation
}
