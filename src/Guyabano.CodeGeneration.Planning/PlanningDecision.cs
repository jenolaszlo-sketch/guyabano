using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>What the loop should do next.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanningAction
{
    /// <summary>Absorb new knowledge by adding work.</summary>
    Expand,
    /// <summary>Replan existing work after an artifact change.</summary>
    Revise,
    /// <summary>Re-check evidence with no structural change.</summary>
    Validate,
    /// <summary>Stop the loop with a recorded reason.</summary>
    Finish,
}

/// <summary>
/// One proposed loop decision. The planner inference proposes it;
/// deterministic validation owns admission. The pinned basis rejects stale
/// decisions the same way patch bases reject stale patches.
/// </summary>
public sealed record PlanningDecision
{
    [JsonPropertyName("designFingerprint")]
    public required string DesignFingerprint { get; init; }

    [JsonPropertyName("artifactRevisions")]
    public required IReadOnlyList<string> ArtifactRevisions { get; init; }

    [JsonPropertyName("workflowVersion")]
    public required string WorkflowVersion { get; init; }

    [JsonPropertyName("action")]
    public required PlanningAction Action { get; init; }

    [JsonPropertyName("motivatingArtifacts")]
    public required IReadOnlyList<string> MotivatingArtifacts { get; init; }

    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }

    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; init; }
}
