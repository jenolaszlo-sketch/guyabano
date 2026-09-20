using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>One stage invocation: a known definition producing a named artifact.</summary>
public sealed record PlannedStage
{
    /// <summary>Definition id; must resolve in the plan's catalogue.</summary>
    [JsonPropertyName("stageId")]
    public required string StageId { get; init; }

    /// <summary>
    /// Full artifact identity (<c>kind/name</c>); the kind must equal the
    /// definition's artifact kind.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Upstream instance names (full identities) this instance consumes.</summary>
    [JsonPropertyName("dependsOn")]
    public required IReadOnlyList<string> DependsOn { get; init; }

    /// <summary>Pinned catalog revisions (<c>kind/name@revision</c>) consumed alongside.</summary>
    [JsonPropertyName("inputArtifacts")]
    public required IReadOnlyList<string> InputArtifacts { get; init; }
}

/// <summary>Where a stage plan came from: required for iterative planning.</summary>
public sealed record PlanningStagePlanProvenance
{
    /// <summary>Inference identity that produced the plan (model name or decision id).</summary>
    [JsonPropertyName("producedBy")]
    public required string ProducedBy { get; init; }

    /// <summary>Catalog revisions consumed while planning.</summary>
    [JsonPropertyName("inputRevisions")]
    public required IReadOnlyList<string> InputRevisions { get; init; }

    /// <summary>Definition catalogue version the plan was built against.</summary>
    [JsonPropertyName("definitionCatalogueVersion")]
    public required string DefinitionCatalogueVersion { get; init; }
}

/// <summary>
/// What planning work an objective needs: an ordered-by-dependency set of
/// stage invocations plus the provenance that makes the plan auditable and
/// revalidatable.
/// </summary>
public sealed record PlanningStagePlan
{
    [JsonPropertyName("stages")]
    public required IReadOnlyList<PlannedStage> Stages { get; init; }

    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }

    [JsonPropertyName("provenance")]
    public required PlanningStagePlanProvenance Provenance { get; init; }
}
