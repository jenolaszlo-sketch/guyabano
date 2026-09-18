namespace Guyabano.Artifacts;

/// <summary>
/// Durable catalog metadata for one workflow: every revision record of every
/// logical planning artifact. Stored as a single content artifact so the
/// catalog is backend-agnostic and gains the content store's integrity checks.
/// </summary>
internal sealed record PlanningArtifactIndex(
    IReadOnlyList<PlanningArtifactRecord> Records)
{
    public static PlanningArtifactIndex Empty { get; } = new([]);
}
