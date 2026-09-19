using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>The pinned catalog versions of the execution design artifacts.</summary>
public sealed record PlannedExecutionDesignVersions(
    PlanningArtifactVersion Graph,
    PlanningArtifactVersion Bindings);
