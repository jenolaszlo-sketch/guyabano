using Penghou.Baize;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Builds the workflow-authoring request from the meta-prompt pack.</summary>
public sealed class WorkflowAuthoringPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<WorkflowAuthoringPromptContext>(templateEngine),
      IPromptBuilder<WorkflowAuthoringPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "workflow-authoring/system.sbn",
        "workflow-authoring/user.sbn");

    protected override void Validate(WorkflowAuthoringPromptContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Request);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CatalogueSummary);
        var hasPrior = !string.IsNullOrWhiteSpace(context.PriorDsl);
        var hasSteps = context.ChangedSteps is { Count: > 0 };
        if (hasPrior != hasSteps)
        {
            throw new ArgumentException(
                "PriorDsl and ChangedSteps must be supplied together.",
                nameof(context));
        }
    }

    protected override object BuildTemplateModel(
        WorkflowAuthoringPromptContext context) => new
        {
            Request = context.Request.Trim(),
            CatalogueSummary = context.CatalogueSummary,
            context.PreviousFailure,
            ExecutionPlan = context.ExecutionPlan?.Trim(),
            PriorDsl = context.PriorDsl?.Trim(),
            ChangedSteps = context.ChangedSteps is { Count: > 0 }
                ? string.Join(", ", context.ChangedSteps)
                : null,
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        WorkflowAuthoringPromptContext context) => null;
}
