using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ComponentRelationshipPlan
{
    public static ComponentRelationshipPlan Empty => new()
    {
        DefinesContractIds = [],
        ImplementsPortContractIds = [],
        ConsumesContractIds = [],
        UsesConcreteTaskIds = [],
        RegistersImplementationTaskIds = [],
        TestsTaskIds = []
    };

    [JsonPropertyName("definesContractIds")]
    public required List<string> DefinesContractIds { get; init; }

    [JsonPropertyName("implementsPortContractIds")]
    public required List<string> ImplementsPortContractIds { get; init; }

    [JsonPropertyName("consumesContractIds")]
    public required List<string> ConsumesContractIds { get; init; }

    [JsonPropertyName("usesConcreteTaskIds")]
    public required List<string> UsesConcreteTaskIds { get; init; }

    [JsonPropertyName("registersImplementationTaskIds")]
    public required List<string> RegistersImplementationTaskIds { get; init; }

    [JsonPropertyName("testsTaskIds")]
    public required List<string> TestsTaskIds { get; init; }
}
