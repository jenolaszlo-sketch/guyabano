using Guyabano.Llm.Prompting;
using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning;

public sealed class CodeGenerationPlanningPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<CodeGenerationPlanningPromptContext>(templateEngine),
      IPromptBuilder<CodeGenerationPlanningPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        SystemPromptName: "code-generation-planning/system.sbn",
        UserTemplateName: "code-generation-planning/user.sbn");

    protected override void Validate(
        CodeGenerationPlanningPromptContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Request))
            throw new ArgumentException(
                "Planning request cannot be empty.",
                nameof(context));

        ArgumentNullException.ThrowIfNull(context.ResponseFormat);
    }

    protected override object BuildTemplateModel(
        CodeGenerationPlanningPromptContext context) => new
        {
            Request = context.Request.Trim(),
            context.PreviousFailure
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        CodeGenerationPlanningPromptContext context) =>
        context.ResponseFormat;
}
