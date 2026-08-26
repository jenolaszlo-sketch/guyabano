using System.Text.Json.Serialization;

namespace Guyabano.Messaging;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowFileCheckStatus
{
    NotRun,
    NotApplicable,
    Running,
    Passed,
    Warning,
    Failed
}
