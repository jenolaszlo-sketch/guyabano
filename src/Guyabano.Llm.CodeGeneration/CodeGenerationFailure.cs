using System.Text.Json.Serialization;

namespace Guyabano.Llm.CodeGeneration;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeGenerationFailure
{
    None,
    NoResponse,
    MissingToolCall,
    InvalidToolArguments,
    TruncatedResponse,
    SchemaValidationFailed,
    DeserializationFailed,
    EmptyResult,
    IncompleteProject,
    OutOfScopeFiles,
    NoChanges,
    FileValidationFailed,
    EmissionFailed
}
