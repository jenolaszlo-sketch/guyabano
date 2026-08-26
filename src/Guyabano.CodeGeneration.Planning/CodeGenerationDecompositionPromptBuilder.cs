using Guyabano.Llm.Prompting;
using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning;

public sealed class CodeGenerationDecompositionPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<CodeGenerationDecompositionPromptContext>(
        templateEngine),
      IPromptBuilder<CodeGenerationDecompositionPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "code-generation-decomposition/system.sbn",
        "code-generation-decomposition/user.sbn");

    protected override void Validate(
        CodeGenerationDecompositionPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context.WorkContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            context.ResultToolName);

        if (context.WorkContext.ParentTask.ExecutionKind !=
            PlanTaskExecutionKind.CodeGeneration)
        {
            throw new ArgumentException(
                "Only code-generation tasks can be decomposed.",
                nameof(context));
        }
    }

    protected override object BuildTemplateModel(
        CodeGenerationDecompositionPromptContext context)
    {
        var work = context.WorkContext;

        return new
        {
            context.ResultToolName,
            work.PlanTitle,
            work.PlanSummary,
            work.Mission,
            work.UseCases,
            work.ParentTask,
            work.Module,
            work.Project,
            work.ProjectDependencies,
            work.Contracts,
            work.AcceptanceCriteria,
            work.ArchitectureNotes,
            work.ComponentDependencies,
            ResolvedDependencies = work.ResolvedDependencies.Artifacts,
            ResolvedDependencyContracts =
                work.ResolvedDependencies.EffectiveContracts,
            work.Decisions
        };
    }

    protected override IReadOnlyList<LlmTool> BuildTools(
        CodeGenerationDecompositionPromptContext context) =>
        context.Tools;
}
