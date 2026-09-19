using System.Text.Json.Serialization;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Execution activity roles: how a planned node is performed (§8 binding artifact).</summary>
public static class PlannedExecutionRoles
{
    public const string Implement = "implement";
    public const string Integrate = "integrate";
    public const string Verify = "verify";
}

/// <summary>
/// One semantic execution step: what to do and what it depends on. The step
/// knows nothing provider-specific; <see cref="PlannedExecutionBinding"/>
/// records how it is performed. Fuwen identifiers are safe: <see cref="Id"/>
/// uses lowercase letters, digits, and underscores only.
/// </summary>
public sealed record PlannedExecutionStep
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("dependsOn")]
    public required IReadOnlyList<string> DependsOn { get; init; }

    [JsonPropertyName("requiredArtifacts")]
    public required IReadOnlyList<string> RequiredArtifacts { get; init; }

    [JsonPropertyName("acceptanceCriteria")]
    public required IReadOnlyList<string> AcceptanceCriteria { get; init; }
}

/// <summary>
/// How one execution step is performed: capability, model profile, pinned
/// context artifacts, and the resolved descriptor reference the Fuwen author
/// must use verbatim.
/// </summary>
public sealed record PlannedExecutionBinding
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("capability")]
    public required string Capability { get; init; }

    [JsonPropertyName("modelProfile")]
    public required string ModelProfile { get; init; }

    [JsonPropertyName("contextArtifacts")]
    public required IReadOnlyList<string> ContextArtifacts { get; init; }

    [JsonPropertyName("descriptor")]
    public required DescriptorReference Descriptor { get; init; }
}

/// <summary>One step's binding, keyed by step id.</summary>
public sealed record PlannedNodeBinding
{
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    [JsonPropertyName("binding")]
    public required PlannedExecutionBinding Binding { get; init; }
}

/// <summary>Semantic execution graph: executable work and its dependencies.</summary>
public sealed record PlannedExecutionGraph
{
    [JsonPropertyName("workflowName")]
    public required string WorkflowName { get; init; }

    [JsonPropertyName("inputType")]
    public required string InputType { get; init; }

    [JsonPropertyName("outputType")]
    public required string OutputType { get; init; }

    [JsonPropertyName("steps")]
    public required IReadOnlyList<PlannedExecutionStep> Steps { get; init; }
}

/// <summary>Per-step bindings for one execution graph.</summary>
public sealed record PlannedExecutionBindings
{
    [JsonPropertyName("workflowName")]
    public required string WorkflowName { get; init; }

    [JsonPropertyName("nodes")]
    public required IReadOnlyList<PlannedNodeBinding> Nodes { get; init; }
}

/// <summary>Execution design: the semantic graph plus its bindings.</summary>
public sealed record PlannedExecutionDesign(
    PlannedExecutionGraph Graph,
    PlannedExecutionBindings Bindings);
