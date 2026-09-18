namespace Guyabano.Artifacts;

/// <summary>
/// The result of invalidating one planning artifact revision: the root version
/// and every descendant revision that transitively pinned an affected upstream
/// version and was therefore marked stale.
/// </summary>
public sealed record PlanningArtifactImpact(
    PlanningArtifactVersion Root,
    IReadOnlyList<PlanningArtifactRecord> Affected);
