using Guyabano.Artifacts;
using Penghou.Guihua.Baize;
using Penghou.Guihua;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>The pinned catalog versions of the execution design artifacts.</summary>
public sealed record PlanningDesignVersions(
    PlanningArtifactVersion Graph,
    PlanningArtifactVersion Bindings);
