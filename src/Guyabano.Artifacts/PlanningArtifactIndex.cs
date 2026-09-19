namespace Guyabano.Artifacts;

/// <summary>
/// Durable catalog metadata for one workflow: every revision record of every
/// logical planning artifact. Stored as a single content artifact so the
/// catalog is backend-agnostic and gains the content store's integrity checks.
/// The generation makes every write content-distinct, so a reverted state
/// still creates a new file and timestamp ordering stays correct.
/// </summary>
internal sealed record PlanningArtifactIndex(
    IReadOnlyList<PlanningArtifactRecord> Records,
    long Generation)
{
    public static PlanningArtifactIndex Empty { get; } = new([], 0);
}
