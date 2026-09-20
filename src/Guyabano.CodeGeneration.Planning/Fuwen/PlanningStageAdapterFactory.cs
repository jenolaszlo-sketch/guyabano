using System.Text;
using Penghou.Guihua.Baize;
using Penghou.Guihua;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Fuwen;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Builds the real stage executors behind the generic runner: the four
/// planning executors bound to their profile/template descriptors, wrapped
/// in binding adapters. This is the construction block extracted from
/// <c>FuwenStagedPlanningService</c>, so adapter behavior matches the
/// hand-wired pipeline exactly.
/// </summary>
public sealed class PlanningStageAdapterFactory(
    ILlmRouter llmRouter,
    IPromptBuilder<DomainDiscoveryPromptContext> domainBuilder,
    IPromptBuilder<SolutionTopologyPromptContext> topologyBuilder,
    IPromptBuilder<ContractDesignPromptContext> contractBuilder,
    IPromptBuilder<ComponentDesignPromptContext> componentBuilder,
    ILlmStructuredOutputRepairer repairer,
    IPromptLoader promptLoader,
    string model,
    FuwenPlanningOptions options)
{
    /// <summary>Creates one executor per built-in stage id.</summary>
    public async Task<IReadOnlyDictionary<string, IPlanningStageExecutor>> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(options);

        var domainProfile = PlanningFuwenDescriptors.StageProfile(
            BuiltInPlanningStages.DomainDiscoveryId, model, options.DomainMaxTokens);
        var topologyProfile = PlanningFuwenDescriptors.StageProfile(
            BuiltInPlanningStages.SolutionTopologyId, model, options.TopologyMaxTokens);
        var contractProfile = PlanningFuwenDescriptors.StageProfile(
            BuiltInPlanningStages.ContractDesignId, model, options.ContractMaxTokens);
        var componentProfile = PlanningFuwenDescriptors.StageProfile(
            BuiltInPlanningStages.ComponentDesignId, model, options.ComponentMaxTokens);
        var domainTemplate = PlanningFuwenDescriptors.Template(
            BuiltInPlanningStages.DomainDiscoveryId,
            "Guyabano.CodeGeneration.Planning.DomainDiscovery",
            await PackAsync("domain-discovery", "system.sbn", cancellationToken).ConfigureAwait(false),
            await PackAsync("domain-discovery", "user.sbn", cancellationToken).ConfigureAwait(false));
        var topologyTemplate = PlanningFuwenDescriptors.Template(
            BuiltInPlanningStages.SolutionTopologyId,
            "Guyabano.CodeGeneration.Planning.SolutionTopology",
            await PackAsync("solution-topology", "system.sbn", cancellationToken).ConfigureAwait(false),
            await PackAsync("solution-topology", "user.sbn", cancellationToken).ConfigureAwait(false));
        var contractTemplate = PlanningFuwenDescriptors.Template(
            BuiltInPlanningStages.ContractDesignId,
            "Guyabano.CodeGeneration.Planning.BoundedContextContractCatalog",
            await PackAsync("contract-design", "system.sbn", cancellationToken).ConfigureAwait(false),
            await PackAsync("contract-design", "user.sbn", cancellationToken).ConfigureAwait(false));
        var componentTemplate = PlanningFuwenDescriptors.Template(
            BuiltInPlanningStages.ComponentDesignId,
            "Guyabano.CodeGeneration.Planning.BoundedContextComponentManifest",
            await PackAsync("component-design", "system.sbn", cancellationToken).ConfigureAwait(false),
            await PackAsync("component-design", "user.sbn", cancellationToken).ConfigureAwait(false));

        var executors = new Dictionary<string, IPlanningStageExecutor>(StringComparer.Ordinal)
        {
            [BuiltInPlanningStages.DomainDiscoveryId] = new DomainDiscoveryStageAdapter(
                new PlanningDomainDiscoveryExecutor(
                    llmRouter, domainBuilder, repairer, model, options.DomainMaxTokens),
                domainProfile,
                domainTemplate),
            [BuiltInPlanningStages.SolutionTopologyId] = new SolutionTopologyStageAdapter(
                new PlanningTopologyExecutor(
                    llmRouter, topologyBuilder, repairer, model, options.TopologyMaxTokens),
                topologyProfile,
                topologyTemplate),
            [BuiltInPlanningStages.ContractDesignId] = new ContractDesignStageAdapter(
                new PlanningContractExecutor(
                    llmRouter, contractBuilder, repairer, model, options.ContractMaxTokens),
                contractProfile,
                contractTemplate),
            [BuiltInPlanningStages.ComponentDesignId] = new ComponentDesignStageAdapter(
                new PlanningComponentExecutor(
                    llmRouter, componentBuilder, repairer, model, options.ComponentMaxTokens),
                componentProfile,
                componentTemplate),
        };
        return executors;
    }

    private async Task<byte[]> PackAsync(
        string pack, string file, CancellationToken cancellationToken) =>
        Encoding.UTF8.GetBytes(await promptLoader.LoadAsync($"{pack}/{file}", cancellationToken)
            .ConfigureAwait(false));
}
