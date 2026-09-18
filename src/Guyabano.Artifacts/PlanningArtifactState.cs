namespace Guyabano.Artifacts;

/// <summary>
/// Lifecycle state of one planning artifact revision. Deliberately small:
/// <see cref="Candidate"/> for freshly produced output, <see cref="Valid"/> for
/// output that passed stage validation, <see cref="Stale"/> when an upstream
/// revision it was derived from changed, and <see cref="Superseded"/> when a
/// newer revision of the same identity was published.
/// </summary>
public enum PlanningArtifactState
{
    Candidate,
    Valid,
    Stale,
    Superseded,
}
