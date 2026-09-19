using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>One dependency edge edit for a surviving execution step.</summary>
public sealed record StepDependencyEdit
{
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    [JsonPropertyName("dependsOn")]
    public required IReadOnlyList<string> DependsOn { get; init; }
}

/// <summary>
/// A proposed delta against one resolved execution design. The model authors
/// the patch; <see cref="StagedExecutionGraphBuilder.Apply"/> validates and
/// merges it deterministically. Steps and bindings the patch does not mention
/// are carried forward unchanged so their Fuwen fingerprints stay stable.
/// </summary>
public sealed record WorkflowPatch
{
    /// <summary>
    /// The <see cref="StagedExecutionDesignIdentity"/> fingerprint of the
    /// design this patch was authored against. Stale bases are rejected.
    /// </summary>
    [JsonPropertyName("baseDesignFingerprint")]
    public required string BaseDesignFingerprint { get; init; }

    /// <summary>
    /// Pinned artifact versions (for example
    /// <c>contracts/billing@3</c>) that motivated the delta. Provenance.
    /// </summary>
    [JsonPropertyName("derivedFromArtifacts")]
    public required IReadOnlyList<string> DerivedFromArtifacts { get; init; }

    /// <summary>Why this change exists.</summary>
    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }

    [JsonPropertyName("addSteps")]
    public required IReadOnlyList<PlannedExecutionStep> AddSteps { get; init; }

    [JsonPropertyName("replaceSteps")]
    public required IReadOnlyList<PlannedExecutionStep> ReplaceSteps { get; init; }

    [JsonPropertyName("removeStepIds")]
    public required IReadOnlyList<string> RemoveStepIds { get; init; }

    [JsonPropertyName("addBindings")]
    public required IReadOnlyList<PlannedNodeBinding> AddBindings { get; init; }

    [JsonPropertyName("replaceBindings")]
    public required IReadOnlyList<PlannedNodeBinding> ReplaceBindings { get; init; }

    /// <summary>
    /// Edge rewiring for surviving steps. Absent means the step keeps its
    /// current dependencies.
    /// </summary>
    [JsonPropertyName("dependencyEdits")]
    public required IReadOnlyList<StepDependencyEdit> DependencyEdits { get; init; }

    /// <summary>
    /// Every step id the patch touches, by addition, replacement, removal,
    /// binding change, or edge edit. Preservation checks treat all other
    /// steps as must-not-change.
    /// </summary>
    public IReadOnlySet<string> AffectedStepIds() => new HashSet<string>(
        AddSteps.Select(step => step.Id)
            .Concat(ReplaceSteps.Select(step => step.Id))
            .Concat(RemoveStepIds)
            .Concat(AddBindings.Select(binding => binding.StepId))
            .Concat(ReplaceBindings.Select(binding => binding.StepId))
            .Concat(DependencyEdits.Select(edit => edit.StepId)),
        StringComparer.Ordinal);
}
