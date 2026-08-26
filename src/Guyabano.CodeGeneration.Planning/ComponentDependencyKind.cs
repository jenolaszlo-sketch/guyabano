using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<ComponentDependencyKind>))]
public enum ComponentDependencyKind
{
    Prerequisite,
    UsesConcreteComponent,
    RegistersImplementation,
    TestsComponent
}
