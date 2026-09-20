namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// The current software pipeline as data: domain discovery, solution
/// topology, contract design, and component design with the exact packs,
/// input kinds, and retry budgets the hand-wired service uses today.
/// </summary>
public static class BuiltInPlanningStages
{
    public const string DomainDiscoveryId = "domain-discovery";
    public const string SolutionTopologyId = "solution-topology";
    public const string ContractDesignId = "contract-design";
    public const string ComponentDesignId = "component-design";

    public static PlanningStageCatalogue Catalogue() => PlanningStageCatalogue.Create(
    [
        new PlanningStageDefinition
        {
            Id = DomainDiscoveryId,
            ArtifactKind = StagedPlanningArtifactPublisher.DomainKind,
            SystemPack = "domain-discovery/system.sbn",
            UserPack = "domain-discovery/user.sbn",
            InputKinds = [],
            OutputSchema = "Guyabano.CodeGeneration.Planning.DomainDiscovery",
            MaxAttempts = 3,
            ModelProfile = DomainDiscoveryId,
        },
        new PlanningStageDefinition
        {
            Id = SolutionTopologyId,
            ArtifactKind = StagedPlanningArtifactPublisher.TopologyKind,
            SystemPack = "solution-topology/system.sbn",
            UserPack = "solution-topology/user.sbn",
            InputKinds = [StagedPlanningArtifactPublisher.DomainKind],
            OutputSchema = "Guyabano.CodeGeneration.Planning.SolutionTopology",
            MaxAttempts = 3,
            ModelProfile = SolutionTopologyId,
        },
        new PlanningStageDefinition
        {
            Id = ContractDesignId,
            ArtifactKind = StagedPlanningArtifactPublisher.ContractKind,
            SystemPack = "contract-design/system.sbn",
            UserPack = "contract-design/user.sbn",
            InputKinds =
            [
                StagedPlanningArtifactPublisher.DomainKind,
                StagedPlanningArtifactPublisher.TopologyKind,
                StagedPlanningArtifactPublisher.ContractKind,
            ],
            OutputSchema = "Guyabano.CodeGeneration.Planning.BoundedContextContractCatalog",
            MaxAttempts = 3,
            ModelProfile = ContractDesignId,
        },
        new PlanningStageDefinition
        {
            Id = ComponentDesignId,
            ArtifactKind = StagedPlanningArtifactPublisher.ComponentKind,
            SystemPack = "component-design/system.sbn",
            UserPack = "component-design/user.sbn",
            InputKinds =
            [
                StagedPlanningArtifactPublisher.DomainKind,
                StagedPlanningArtifactPublisher.TopologyKind,
                StagedPlanningArtifactPublisher.ContractKind,
                StagedPlanningArtifactPublisher.ComponentKind,
            ],
            OutputSchema = "Guyabano.CodeGeneration.Planning.BoundedContextComponentManifest",
            MaxAttempts = 3,
            ModelProfile = ComponentDesignId,
        },
    ]);
}
