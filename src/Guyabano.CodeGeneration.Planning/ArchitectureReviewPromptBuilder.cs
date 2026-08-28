using Guyabano.Llm.Prompting;
using Penghou.Baize;
using System.Text.Json;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureReviewPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<ArchitectureReviewPromptContext>(templateEngine),
      IPromptBuilder<ArchitectureReviewPromptContext>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    protected override PromptTemplate Template { get; } = new(
        "architecture-review/system.sbn",
        "architecture-review/user.sbn");

    protected override void Validate(ArchitectureReviewPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Plan);
        ArgumentNullException.ThrowIfNull(context.ResponseFormat);
    }

    protected override object BuildTemplateModel(
        ArchitectureReviewPromptContext context) => new
        {
            context.ReviewPass,
            context.PreviousFailure,
            PreviousReviewJson = context.PreviousReview is null
                ? null
                : JsonSerializer.Serialize(
                    context.PreviousReview,
                    JsonOptions),
            PlanJson = JsonSerializer.Serialize(context.Plan, JsonOptions),
            SessionContext = SessionContextDisclosureScope.Current
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        ArchitectureReviewPromptContext context) => context.ResponseFormat;
}
