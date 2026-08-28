using Guyabano.Llm.Prompting;
using Penghou.Baize;
using System.Text.Json;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureDecisionIntegrationPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<ArchitectureDecisionIntegrationPromptContext>(templateEngine),
      IPromptBuilder<ArchitectureDecisionIntegrationPromptContext>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    protected override PromptTemplate Template { get; } = new(
        "architecture-decision-integration/system.sbn",
        "architecture-decision-integration/user.sbn");

    protected override void Validate(
        ArchitectureDecisionIntegrationPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Plan);
        ArgumentNullException.ThrowIfNull(context.ResolvedReview);
        ArgumentNullException.ThrowIfNull(context.ResolvedDecisions);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ResultToolName);
    }

    protected override object BuildTemplateModel(
        ArchitectureDecisionIntegrationPromptContext context) => new
        {
            context.ResultToolName,
            context.PreviousFailure,
            PlanJson = JsonSerializer.Serialize(context.Plan, JsonOptions),
            ResolvedReviewJson = JsonSerializer.Serialize(
                context.ResolvedReview,
                JsonOptions),
            ResolvedDecisionsJson = JsonSerializer.Serialize(
                context.ResolvedDecisions,
                JsonOptions),
            SessionContext = SessionContextDisclosureScope.Current
        };

    protected override IReadOnlyList<LlmTool> BuildTools(
        ArchitectureDecisionIntegrationPromptContext context) => context.Tools;
}
