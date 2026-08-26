namespace Guyabano.CodeGeneration.Planning;

public sealed record StagedPlanningArtifacts(
    DomainDiscovery Domain,
    SolutionTopology Topology,
    IReadOnlyList<BoundedContextContractCatalog> ContractCatalogs,
    IReadOnlyList<BoundedContextComponentManifest> ComponentManifests);
