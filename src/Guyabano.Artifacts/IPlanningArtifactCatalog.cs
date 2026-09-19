namespace Guyabano.Artifacts;

/// <summary>
/// Revisioned catalog of planning artifacts for a workflow. Publishing assigns
/// stable identities and monotonically increasing revisions, records upstream
/// provenance, and supports impact analysis so an upstream change can mark only
/// its affected descendants stale.
/// </summary>
public interface IPlanningArtifactCatalog
{
    /// <summary>
    /// Persists a new revision of <see cref="PublishPlanningArtifactRequest{TPayload}.Key"/>,
    /// superseding every earlier revision of the same identity.
    /// </summary>
    Task<PlanningArtifactRecord> PublishAsync<TPayload>(
        PublishPlanningArtifactRequest<TPayload> request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the record for an exact revision, or null when it does not exist.</summary>
    Task<PlanningArtifactRecord?> GetAsync(
        string workflowId,
        PlanningArtifactVersion version,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the highest revision of a logical artifact, or null when it has none.</summary>
    Task<PlanningArtifactRecord?> GetCurrentAsync(
        string workflowId,
        PlanningArtifactKey key,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every revision of a logical artifact, oldest first.</summary>
    Task<IReadOnlyList<PlanningArtifactRecord>> ListRevisionsAsync(
        string workflowId,
        PlanningArtifactKey key,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the current (highest) revision of every logical artifact in the workflow.</summary>
    Task<IReadOnlyList<PlanningArtifactRecord>> ListCurrentAsync(
        string workflowId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the stored payload for a catalog record.</summary>
    Task<TPayload> ReadPayloadAsync<TPayload>(
        PlanningArtifactRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs impact analysis after <paramref name="changed"/> was published.
    /// Every other revision of the same logical identity, and every revision
    /// that transitively depends on one of them, is marked stale;
    /// <paramref name="changed"/> itself stays valid. Unrelated branches are
    /// preserved. Returns the affected revisions.
    /// </summary>
    Task<PlanningArtifactImpact> InvalidateAsync(
        string workflowId,
        PlanningArtifactVersion changed,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a stale revision valid again after re-derivation from the current
    /// upstream confirms its content is unchanged. This is how regeneration
    /// retains unaffected branches instead of republishing them.
    /// </summary>
    Task<PlanningArtifactRecord> RevalidateAsync(
        string workflowId,
        PlanningArtifactVersion version,
        CancellationToken cancellationToken = default);
}
