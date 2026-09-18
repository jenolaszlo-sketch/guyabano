namespace Guyabano.Artifacts;

/// <summary>
/// Requests a new revision of a logical planning artifact. The catalog assigns
/// the revision number, persists the payload through the content store, and
/// records the declared upstream versions as provenance.
/// </summary>
public sealed record PublishPlanningArtifactRequest<TPayload>(
    string WorkflowId,
    PlanningArtifactKey Key,
    int SchemaVersion,
    string ProducedBy,
    TPayload Payload,
    IReadOnlyList<PlanningArtifactVersion>? Inputs = null,
    PlanningArtifactState State = PlanningArtifactState.Candidate)
{
    /// <summary>The planning session that produced this revision, when known.</summary>
    public string? SessionId { get; init; }
}
