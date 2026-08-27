using Penghou.Baize;

namespace Guyabano.Llm.Prompting;

public sealed class CodeGenerationTaskPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<CodeGenerationTaskPromptContext>(templateEngine),
      IPromptBuilder<CodeGenerationTaskPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        SystemPromptName: "code-generation-task/system.sbn",
        UserTemplateName: "code-generation-task/user.sbn");

    protected override void Validate(
        CodeGenerationTaskPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Task);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Task.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Task.Objective);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            context.Task.ProjectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ResultToolName);

        if (context.Tools.Count != 1 ||
            !context.Tools[0].Name.Equals(
                context.ResultToolName,
                StringComparison.Ordinal))
            throw new ArgumentException(
                "A task-generation prompt requires exactly its result tool.",
                nameof(context));
    }

    protected override object BuildTemplateModel(
        CodeGenerationTaskPromptContext context) => new
        {
            context.ResultToolName,
            context.Task.OriginalRequest,
            context.Task.TaskId,
            context.Task.TaskTitle,
            context.Task.ParentTaskId,
            context.Task.Objective,
            context.Task.SolutionName,
            context.Task.SolutionPath,
            context.Task.ProjectName,
            context.Task.ProjectPath,
            context.Task.ProjectDirectory,
            context.Task.RootNamespace,
            context.Task.TargetFramework,
            context.Task.ModuleName,
            context.Task.ModuleResponsibilities,
            context.Task.Deliverables,
            context.Task.Contracts,
            context.Task.AcceptanceCriteria,
            context.Task.Decisions,
            ArchitectureNotes = context.Task.ArchitectureNotes ?? [],
            ImplementationRequirements =
                context.Task.ImplementationRequirements ?? [],
            Artifacts = context.Task.Artifacts ?? [],
            context.Task.Files,
            context.Task.Retry,
            context.Task.AllowBuildArtifacts
        };

    protected override IReadOnlyList<LlmTool> BuildTools(
        CodeGenerationTaskPromptContext context) =>
        context.Tools;

    protected override IReadOnlyDictionary<string, object?> BuildMetadata(
        CodeGenerationTaskPromptContext context)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["guyabano.task_id"] = context.Task.TaskId
        };
        Add(metadata, "guyabano.session_id", context.Task.SessionId);
        Add(metadata, "guyabano.workflow_run_id", context.Task.WorkflowRunId);
        Add(metadata, "guyabano.workflow_step_key", context.Task.WorkflowStepKey);
        return metadata;
    }

    private static void Add(
        IDictionary<string, object?> metadata,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value;
    }
}
