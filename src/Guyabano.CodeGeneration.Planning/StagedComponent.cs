using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class StagedComponent
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("moduleName")]
    public required string ModuleName { get; init; }

    [JsonPropertyName("projectName")]
    public required string ProjectName { get; init; }

    [JsonPropertyName("files")]
    public required List<string> Files { get; init; }

    [JsonPropertyName("responsibilities")]
    public required List<string> Responsibilities { get; init; }

    [JsonPropertyName("definesContractNames")]
    public required List<string> DefinesContractNames { get; init; }

    [JsonPropertyName("implementsPortNames")]
    public required List<string> ImplementsPortNames { get; init; }

    [JsonPropertyName("consumesContractNames")]
    public required List<string> ConsumesContractNames { get; init; }

    [JsonPropertyName("usesConcreteComponentNames")]
    public required List<string> UsesConcreteComponentNames { get; init; }

    [JsonPropertyName("registersImplementationNames")]
    public required List<string> RegistersImplementationNames { get; init; }

    [JsonPropertyName("testsComponentNames")]
    public required List<string> TestsComponentNames { get; init; }

    [JsonPropertyName("capabilityNames")]
    public required List<string> CapabilityNames { get; init; }

    [JsonPropertyName("acceptanceCriterionIds")]
    public required List<string> AcceptanceCriterionIds { get; init; }

    [JsonPropertyName("lifetime")]
    public required string Lifetime { get; init; }

    [JsonPropertyName("complexityPoints")]
    public required int ComplexityPoints { get; init; }

    [JsonPropertyName("verificationKinds")]
    public required List<string> VerificationKinds { get; init; }
}
