using Penghou.Baize;
using Penghou.Baize.Tools.Schema;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Builds the planning-bootstrap request from the meta-prompt pack.</summary>
public sealed class PlanningBootstrapPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<PlanningBootstrapPromptContext>(templateEngine),
      IPromptBuilder<PlanningBootstrapPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "planning-bootstrap/system.sbn",
        "planning-bootstrap/user.sbn");

    protected override void Validate(PlanningBootstrapPromptContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.StageCatalogue);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CatalogueVersion);
        ArgumentNullException.ThrowIfNull(context.CurrentRevisions);
    }

    protected override object BuildTemplateModel(
        PlanningBootstrapPromptContext context) => new
        {
            Goal = context.Goal.Trim(),
            StageCatalogue = context.StageCatalogue,
            CatalogueVersion = context.CatalogueVersion.Trim(),
            CurrentRevisions = context.CurrentRevisions.Count == 0
                ? "(none)"
                : string.Join(", ", context.CurrentRevisions),
            context.PreviousFailure,
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        PlanningBootstrapPromptContext context) =>
        LlmResponseFormat.JsonSchema(JsonSchemaGenerator.GenerateSchemaJson<PlanningStagePlan>());
}
