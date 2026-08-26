using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<TaskDecompositionStatus>))]
public enum TaskDecompositionStatus
{
    Ready,
    ArchitectureGap
}
