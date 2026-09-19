using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Lifecycle of one planning loop run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanningLoopStatus
{
    /// <summary>Iterating.</summary>
    Running,
    /// <summary>Stopped by an admitted finish decision or backstop.</summary>
    Finished,
    /// <summary>Stopped by a policy bound.</summary>
    Exhausted,
    /// <summary>Stopped by a failure (proposal, authoring, execution, divergence).</summary>
    Failed,
}

/// <summary>
/// Durable loop state, revised as a <c>planning-checkpoint</c> catalog
/// artifact every iteration. A restarted driver reloads the latest revision
/// and resumes instead of repeating work. The design and admitted DSL travel
/// with the checkpoint so resume needs no model output; the plan itself is
/// recovered by recompiling the DSL, which the canonical compile chain
/// guarantees reproduces identically.
/// </summary>
public sealed record PlanningCheckpoint
{
    [JsonPropertyName("iteration")]
    public required int Iteration { get; init; }

    [JsonPropertyName("workflowId")]
    public required string WorkflowId { get; init; }

    [JsonPropertyName("workflowVersion")]
    public required string WorkflowVersion { get; init; }

    [JsonPropertyName("designFingerprint")]
    public required string DesignFingerprint { get; init; }

    [JsonPropertyName("design")]
    public required PlannedExecutionDesign Design { get; init; }

    [JsonPropertyName("dsl")]
    public required string Dsl { get; init; }

    [JsonPropertyName("modelCalls")]
    public required int ModelCalls { get; init; }

    [JsonPropertyName("mutations")]
    public required int Mutations { get; init; }

    [JsonPropertyName("structuralIterations")]
    public required int StructuralIterations { get; init; }

    /// <summary>Null when no provider has reported usage yet.</summary>
    [JsonPropertyName("reportedTokens")]
    public int? ReportedTokens { get; init; }

    [JsonPropertyName("consecutiveNoOps")]
    public required int ConsecutiveNoOps { get; init; }

    [JsonPropertyName("knownArtifactRevisions")]
    public required IReadOnlyList<string> KnownArtifactRevisions { get; init; }

    [JsonPropertyName("decisionLog")]
    public required IReadOnlyList<string> DecisionLog { get; init; }

    [JsonPropertyName("status")]
    public required PlanningLoopStatus Status { get; init; }

    [JsonPropertyName("terminalReason")]
    public string? TerminalReason { get; init; }
}
