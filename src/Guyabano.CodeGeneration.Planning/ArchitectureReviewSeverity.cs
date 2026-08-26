using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<ArchitectureReviewSeverity>))]
public enum ArchitectureReviewSeverity
{
    Warning,
    Blocking
}
