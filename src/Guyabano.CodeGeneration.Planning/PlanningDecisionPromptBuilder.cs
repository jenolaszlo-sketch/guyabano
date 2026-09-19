using Penghou.Baize;
using Penghou.Baize.Tools.Schema;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Builds the planning-decision request from the meta-prompt pack.</summary>
public sealed class PlanningDecisionPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<PlanningDecisionPromptContext>(templateEngine),
      IPromptBuilder<PlanningDecisionPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "planning-decision/system.sbn",
        "planning-decision/user.sbn");

    protected override void Validate(PlanningDecisionPromptContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ExecutionPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.DesignFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.WorkflowVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.EvidenceSummary);
        ArgumentNullException.ThrowIfNull(context.FreshArtifacts);
        if (context.IterationsRemaining < 0 ||
            context.MutationsRemaining < 0 ||
            context.StructuralRemaining < 0 ||
            context.ModelCallsRemaining < 0)
        {
            throw new ArgumentException(
                "Remaining budgets must not be negative.",
                nameof(context));
        }
    }

    protected override object BuildTemplateModel(
        PlanningDecisionPromptContext context) => new
        {
            Goal = context.Goal.Trim(),
            ExecutionPlan = context.ExecutionPlan.Trim(),
            DesignFingerprint = context.DesignFingerprint.Trim(),
            WorkflowVersion = context.WorkflowVersion.Trim(),
            WorkflowComplete = context.WorkflowComplete ? "yes" : "no",
            EvidenceSummary = context.EvidenceSummary.Trim(),
            FreshArtifacts = context.FreshArtifacts.Count == 0
                ? "(none)"
                : string.Join(", ", context.FreshArtifacts),
            SupersededPins = context.SupersededPins.Count == 0
                ? "(none)"
                : string.Join("; ", context.SupersededPins),
            IterationsRemaining = context.IterationsRemaining,
            MutationsRemaining = context.MutationsRemaining,
            StructuralRemaining = context.StructuralRemaining,
            ModelCallsRemaining = context.ModelCallsRemaining,
            TokensRemaining = context.TokensRemaining?.ToString(),
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        PlanningDecisionPromptContext context) =>
        LlmResponseFormat.JsonSchema(JsonSchemaGenerator.GenerateSchemaJson<PlanningDecision>());
}
