using Penghou.Baize;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ComponentDesignPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<ComponentDesignPromptContext>(templateEngine),
      IPromptBuilder<ComponentDesignPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "component-design/system.sbn",
        "component-design/user.sbn");

    protected override void Validate(ComponentDesignPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Domain);
        ArgumentNullException.ThrowIfNull(context.Topology);
        ArgumentNullException.ThrowIfNull(context.BoundedContext);
        ArgumentNullException.ThrowIfNull(context.ResponseFormat);
    }

    protected override object BuildTemplateModel(
        ComponentDesignPromptContext context) => new
        {
            MissionJson = PlanningPromptJson.Serialize(
                context.Domain.Mission),
            ContextJson = PlanningPromptJson.Serialize(
                context.BoundedContext),
            UseCasesJson = PlanningPromptJson.Serialize(
                context.Domain.UseCases.Where(useCase =>
                    context.BoundedContext.CapabilityNames.Contains(
                        useCase.CapabilityName,
                        StringComparer.Ordinal))),
            AcceptanceCriteriaJson = PlanningPromptJson.Serialize(
                context.Domain.UseCases
                    .Where(useCase =>
                        context.BoundedContext.CapabilityNames.Contains(
                            useCase.CapabilityName,
                            StringComparer.Ordinal))
                    .SelectMany(useCase => useCase.AcceptanceCriteria.Select(
                        criterion => new
                        {
                            Id = RequirementIdentity.AcceptanceCriterionId(
                                useCase,
                                criterion),
                            UseCaseId = RequirementIdentity.UseCaseId(useCase),
                            UseCase = useCase.Name,
                            Capability = useCase.CapabilityName,
                            criterion.Scenario,
                            criterion.Given,
                            criterion.When,
                            criterion.Then,
                            criterion.VerificationKinds
                        }))),
            ModulesJson = PlanningPromptJson.Serialize(
                context.Topology.Modules.Where(module =>
                    module.BoundedContextName.Equals(
                        context.BoundedContext.Name,
                        StringComparison.Ordinal))),
            ContractsJson = PlanningPromptJson.Serialize(
                context.ContractCatalogs.Where(catalog =>
                    catalog.BoundedContextName.Equals(
                        context.BoundedContext.Name,
                        StringComparison.Ordinal) ||
                    context.UpstreamManifests.Any(manifest =>
                        manifest.BoundedContextName.Equals(
                            catalog.BoundedContextName,
                            StringComparison.Ordinal)))),
            UpstreamComponentsJson = PlanningPromptJson.Serialize(
                context.UpstreamManifests),
            context.PreviousFailure
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        ComponentDesignPromptContext context) => context.ResponseFormat;
}
