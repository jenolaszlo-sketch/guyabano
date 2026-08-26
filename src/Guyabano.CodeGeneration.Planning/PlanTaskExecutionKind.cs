using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<PlanTaskExecutionKind>))]
public enum PlanTaskExecutionKind
{
    Scaffolding,
    CodeGeneration
}
