using Penghou.Baize;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureGapResolutionPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<ArchitectureGapResolutionPromptContext>(templateEngine),
      IPromptBuilder<ArchitectureGapResolutionPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "architecture-gap-resolution/system.sbn",
        "architecture-gap-resolution/user.sbn");

    protected override void Validate(
        ArchitectureGapResolutionPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Plan);
        ArgumentNullException.ThrowIfNull(context.Finding);
        ArgumentNullException.ThrowIfNull(context.Practices);
        ArgumentNullException.ThrowIfNull(context.ResponseFormat);
    }

    protected override object BuildTemplateModel(
        ArchitectureGapResolutionPromptContext context) => new
        {
            PlanJson = PlanningPromptJson.Serialize(context.Plan),
            FindingJson = PlanningPromptJson.Serialize(context.Finding),
            PracticesJson = PlanningPromptJson.Serialize(context.Practices),
            context.DecisionId,
            context.PreviousFailure
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        ArchitectureGapResolutionPromptContext context) =>
        context.ResponseFormat;
}
