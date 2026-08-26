using Penghou.Baize;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ContractDesignPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<ContractDesignPromptContext>(templateEngine),
      IPromptBuilder<ContractDesignPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "contract-design/system.sbn",
        "contract-design/user.sbn");

    protected override void Validate(ContractDesignPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Domain);
        ArgumentNullException.ThrowIfNull(context.Topology);
        ArgumentNullException.ThrowIfNull(context.BoundedContext);
        ArgumentNullException.ThrowIfNull(context.ResponseFormat);
    }

    protected override object BuildTemplateModel(
        ContractDesignPromptContext context) => new
        {
            MissionJson = PlanningPromptJson.Serialize(
                context.Domain.Mission),
            ContextJson = PlanningPromptJson.Serialize(
                context.BoundedContext),
            CapabilitiesJson = PlanningPromptJson.Serialize(
                context.Domain.Capabilities.Where(capability =>
                    context.BoundedContext.CapabilityNames.Contains(
                        capability.Name,
                        StringComparer.Ordinal))),
            UseCasesJson = PlanningPromptJson.Serialize(
                context.Domain.UseCases.Where(useCase =>
                    context.BoundedContext.CapabilityNames.Contains(
                        useCase.CapabilityName,
                        StringComparer.Ordinal))),
            ModulesJson = PlanningPromptJson.Serialize(
                context.Topology.Modules.Where(module =>
                    module.BoundedContextName.Equals(
                        context.BoundedContext.Name,
                        StringComparison.Ordinal))),
            UpstreamContractsJson = PlanningPromptJson.Serialize(
                context.UpstreamCatalogs),
            context.PreviousFailure
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        ContractDesignPromptContext context) => context.ResponseFormat;
}
