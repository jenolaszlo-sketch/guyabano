using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectRole>))]
public enum ProjectRole
{
    Domain,
    Application,
    Contracts,
    Adapter,
    CompositionRoot,
    Test,
    Tooling
}
