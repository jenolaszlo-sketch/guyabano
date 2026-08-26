using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class CodeGenerationLeafTask
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("objective")]
    public required string Objective { get; init; }

    [JsonPropertyName("complexityPoints")]
    [SchemaDescription("Leaf complexity using 1 or 2 points only.")]
    public required int ComplexityPoints { get; init; }

    [JsonPropertyName("dependsOn")]
    [SchemaDescription("Sibling leaf task IDs that must complete first.")]
    public required List<string> DependsOn { get; init; }

    [JsonPropertyName("contractIds")]
    [SchemaDescription("Existing architecture contract IDs used by this leaf. Never invent IDs.")]
    public required List<string> ContractIds { get; init; }

    [JsonPropertyName("acceptanceCriterionIds")]
    [SchemaDescription("Existing parent acceptance criterion IDs verified by this leaf.")]
    public required List<string> AcceptanceCriterionIds { get; init; }

    [JsonPropertyName("decisionIds")]
    [SchemaDescription("Existing ADR IDs constraining this leaf. Never invent IDs.")]
    public required List<string> DecisionIds { get; init; }

    [JsonPropertyName("implementationRequirements")]
    [SchemaDescription("Exact implementation rules left after architecture decisions; do not include source code.")]
    public required List<string> ImplementationRequirements { get; init; }

    [JsonPropertyName("artifacts")]
    public required List<DecomposedArtifactPlan> Artifacts { get; init; }

    [JsonPropertyName("verificationKinds")]
    public required List<string> VerificationKinds { get; init; }
}
