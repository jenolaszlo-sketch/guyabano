using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanningFailure
{
    None,
    NoResponse,
    MissingToolCall,
    InvalidToolArguments,
    TruncatedResponse,
    SchemaValidationFailed,
    DeserializationFailed,
    InvalidPlan
}
