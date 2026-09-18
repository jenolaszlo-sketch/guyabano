namespace Guyabano.Artifacts;

/// <summary>
/// One durable revision of one logical planning artifact, together with the
/// provenance needed to reason about the artifact dependency graph: the exact
/// upstream versions it was derived from and the workflow node that produced it.
/// </summary>
public sealed record PlanningArtifactRecord(
    PlanningArtifactVersion Version,
    ArtifactReference Content,
    string WorkflowId,
    string ProducedBy,
    IReadOnlyList<PlanningArtifactVersion> Inputs,
    PlanningArtifactState State,
    DateTimeOffset CreatedAt)
{
    /// <summary>The planning session that produced this revision, when known.</summary>
    public string? SessionId { get; init; }

    /// <summary>The revision number of the logical artifact.</summary>
    public int Revision => Version.Revision;

    /// <summary>The stable logical identity of the artifact.</summary>
    public PlanningArtifactKey Key => Version.Key;
}
