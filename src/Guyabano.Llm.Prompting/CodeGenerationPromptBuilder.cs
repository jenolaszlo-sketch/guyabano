using Penghou.Baize;

namespace Guyabano.Llm.Prompting;

public sealed class CodeGenerationPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<CodeGenerationPromptContext>(templateEngine),
      IPromptBuilder<CodeGenerationPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        SystemPromptName: "code-generation/system.sbn",
        UserTemplateName: "code-generation/user.sbn");

    protected override void Validate(CodeGenerationPromptContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Task))
            throw new ArgumentException("Task cannot be empty.", nameof(context));

        if (string.IsNullOrWhiteSpace(context.ResultToolName))
            throw new ArgumentException(
                "Result tool name cannot be empty.",
                nameof(context));

        if (string.IsNullOrWhiteSpace(context.ProjectName))
            throw new ArgumentException(
                "Project name cannot be empty.",
                nameof(context));

        if (string.IsNullOrWhiteSpace(context.RootNamespace))
            throw new ArgumentException(
                "Root namespace cannot be empty.",
                nameof(context));

        if (string.IsNullOrWhiteSpace(context.TargetFramework))
            throw new ArgumentException(
                "Target framework cannot be empty.",
                nameof(context));
    }

    protected override object BuildTemplateModel(
        CodeGenerationPromptContext context) => new
        {
            Task = context.Task.Trim(),
            context.ProjectContext,
            Files = context.Files ?? [],
            context.ResultToolName,
            context.ProjectName,
            context.RootNamespace,
            context.TargetFramework,
            SolutionFile = $"{context.ProjectName}.sln",
            ApplicationProjectFile =
            $"src/{context.ProjectName}/{context.ProjectName}.csproj",
            TestProjectName = $"{context.ProjectName}.Tests",
            TestProjectFile =
            $"tests/{context.ProjectName}.Tests/{context.ProjectName}.Tests.csproj"
        };

    protected override IReadOnlyList<LlmTool> BuildTools(
        CodeGenerationPromptContext context) =>
        context.Tools ?? [];
}
