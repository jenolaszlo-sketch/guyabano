using Penghou.Baize;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

public sealed class PlanningGapResolutionPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<PlanningGapResolutionPromptContext>(templateEngine),
      IPromptBuilder<PlanningGapResolutionPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "planning-gap-resolution/system.sbn",
        "planning-gap-resolution/user.sbn");

    protected override void Validate(PlanningGapResolutionPromptContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.OriginalRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Issue);
        ArgumentNullException.ThrowIfNull(context.ResponseFormat);
    }

    protected override object BuildTemplateModel(
        PlanningGapResolutionPromptContext context) => new
        {
            context.OriginalRequest,
            context.Stage,
            context.CurrentArtifactJson,
            context.Issue,
            context.PreviousFailure
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        PlanningGapResolutionPromptContext context) => context.ResponseFormat;
}
