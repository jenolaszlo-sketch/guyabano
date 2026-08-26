using Penghou.Baize;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

public sealed class SolutionTopologyPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<SolutionTopologyPromptContext>(templateEngine),
      IPromptBuilder<SolutionTopologyPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "solution-topology/system.sbn",
        "solution-topology/user.sbn");

    protected override void Validate(SolutionTopologyPromptContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.OriginalRequest);
        ArgumentNullException.ThrowIfNull(context.Domain);
        ArgumentNullException.ThrowIfNull(context.ResponseFormat);
    }

    protected override object BuildTemplateModel(
        SolutionTopologyPromptContext context) => new
        {
            DomainJson = PlanningPromptJson.Serialize(context.Domain),
            context.PreviousFailure
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        SolutionTopologyPromptContext context) => context.ResponseFormat;
}
