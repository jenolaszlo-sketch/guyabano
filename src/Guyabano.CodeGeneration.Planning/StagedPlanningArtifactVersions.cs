using Guyabano.Artifacts;
using Penghou.Guihua.Baize;
using Penghou.Guihua;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// The pinned catalog versions produced by publishing one staged planning
/// result: the domain discovery artifact, the solution topology, and the
/// contract and component artifacts keyed by bounded-context name.
/// </summary>
public sealed record StagedPlanningArtifactVersions(
    PlanningArtifactVersion Domain,
    PlanningArtifactVersion Topology,
    IReadOnlyDictionary<string, PlanningArtifactVersion> Contracts,
    IReadOnlyDictionary<string, PlanningArtifactVersion> Components);
