using Penghou.Baize;
using Penghou.Baize.Tools.Schema;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Builds the workflow-patch proposal request from the meta-prompt pack.</summary>
public sealed class WorkflowPatchPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<WorkflowPatchPromptContext>(templateEngine),
      IPromptBuilder<WorkflowPatchPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "workflow-patch/system.sbn",
        "workflow-patch/user.sbn");

    protected override void Validate(WorkflowPatchPromptContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ExecutionPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.BaseDesignFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CatalogueSummary);
        if (context.ChangedArtifacts is not { Count: > 0 })
        {
            throw new ArgumentException(
                "Changed artifacts must not be empty.",
                nameof(context));
        }
    }

    protected override object BuildTemplateModel(
        WorkflowPatchPromptContext context) => new
        {
            Goal = context.Goal.Trim(),
            ExecutionPlan = context.ExecutionPlan.Trim(),
            BaseDesignFingerprint = context.BaseDesignFingerprint.Trim(),
            ChangedArtifacts = string.Join(", ", context.ChangedArtifacts),
            CatalogueSummary = context.CatalogueSummary,
            context.PreviousFailure,
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        WorkflowPatchPromptContext context) =>
        LlmResponseFormat.JsonSchema(JsonSchemaGenerator.GenerateSchemaJson<WorkflowPatch>());
}
