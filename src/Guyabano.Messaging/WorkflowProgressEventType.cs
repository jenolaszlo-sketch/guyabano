using System.Text.Json.Serialization;

namespace Guyabano.Messaging;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowProgressEventType
{
    Started,
    Completed,
    Failed,
    Canceled
}
