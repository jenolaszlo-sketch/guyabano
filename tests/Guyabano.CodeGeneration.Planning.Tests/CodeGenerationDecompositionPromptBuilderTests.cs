using FluentAssertions;
using Guyabano.Llm.Prompting;
using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class CodeGenerationDecompositionPromptBuilderTests
{
    [Fact]
    public async Task BuildAsync_UsesParentArchitectureSliceAndOneTool()
    {
        var engine = new RecordingTemplateEngine();
        var builder = new CodeGenerationDecompositionPromptBuilder(engine);
        var plan = PlanTestData.Create();
        plan.ArchitectureNotes.Add(CreateTitleLengthNote());
        var tool = new LlmTool(
            "return_task_decomposition",
            "Returns leaves.",
            """{"type":"object"}""");

        var dependencies = new ResolvedDependencyContext(
                [
                    new ResolvedArtifactDependency(
                        "T-Store",
                        "T-Store-L1",
                        "src/Todo.Api/Data/InMemoryTodoStore.cs",
                        "CSharpClass",
                        "Todo.Api.Data",
                        ["InMemoryTodoStore"],
                        ["CONTRACT-TODO-SERVICE"])
                ]);
        var workContext = new ComponentWorkContextBuilder().Build(
            plan,
            plan.Tasks.Single().Id,
            dependencies);
        var request = await builder.BuildAsync(
            new CodeGenerationDecompositionPromptContext(
                workContext,
                tool.Name,
                [tool]),
            TestContext.Current.CancellationToken);

        request.Tools.Should().ContainSingle().Which.Should().Be(tool);
        engine.TemplateNames.Should().Equal(
            "code-generation-decomposition/system.sbn",
            "code-generation-decomposition/user.sbn");
        engine.ParentTaskIds.Should().OnlyContain(id => id == "TASK-001");
        engine.ToolNames.Should().OnlyContain(
            name => name == "return_task_decomposition");
        engine.ResolvedTypeNames.Should().Contain(
            "Todo.Api.Data.InMemoryTodoStore");
    }

    [Fact]
    public async Task BuildAsync_RendersResolvedTypesWithProductionTemplate()
    {
        var builder = new CodeGenerationDecompositionPromptBuilder(
            new ScribanPromptTemplateEngine(
                new FilePromptLoader(Path.Combine(
                    AppContext.BaseDirectory,
                    "prompts"))));
        var plan = PlanTestData.Create();
        plan.ArchitectureNotes.Add(CreateTitleLengthNote());
        var tool = new LlmTool(
            "return_task_decomposition",
            "Returns leaves.",
            """{"type":"object"}""");

        var dependencies = new ResolvedDependencyContext(
                [
                    new ResolvedArtifactDependency(
                        "T-Store",
                        "T-Store-L1",
                        "src/Todo.Api/Data/InMemoryTodoStore.cs",
                        "CSharpClass",
                        "Todo.Api.Data",
                        ["InMemoryTodoStore"],
                        ["CONTRACT-TODO-SERVICE"])
                ]);
        var workContext = new ComponentWorkContextBuilder().Build(
            plan,
            plan.Tasks.Single().Id,
            dependencies);
        var request = await builder.BuildAsync(
            new CodeGenerationDecompositionPromptContext(
                workContext,
                tool.Name,
                [tool]),
            TestContext.Current.CancellationToken);

        request.Messages[1].Text().Should().Contain(
            "Todo.Api.Data.InMemoryTodoStore");
        request.Messages[1].Text().Should().Contain(
            "src/Todo.Api/Data/InMemoryTodoStore.cs");
        request.Messages[1].Text().Should().Contain(
            "CONTRACT-TODO-SERVICE");
        request.Messages[1].Text().Should().Contain(
            "Todo Create(string title)");
        request.Messages[1].Text().Should().Contain(
            "Limit todo titles to 200 characters");
    }

    private static ArchitectureNote CreateTitleLengthNote() => new()
    {
        Id = "NOTE-TITLE-LENGTH",
        Category = ArchitectureNoteCategory.InferredDomainConstraint,
        Subject = "Todo title length",
        MissingInformation = "The maximum title length was unspecified.",
        Decision = "Limit todo titles to 200 characters.",
        Reasons = ["Titles should remain concise and bounded."],
        Impact = "Longer titles are rejected.",
        AffectedIds = ["CONTRACT-TODO-SERVICE", "TASK-001"],
        UserOverridable = true
    };

    private sealed class RecordingTemplateEngine : IPromptTemplateEngine
    {
        public List<string> TemplateNames { get; } = [];
        public List<string?> ParentTaskIds { get; } = [];
        public List<string?> ToolNames { get; } = [];
        public List<string> ResolvedTypeNames { get; } = [];

        public Task<string> RenderAsync(
            string templateName,
            object model,
            CancellationToken cancellationToken = default)
        {
            TemplateNames.Add(templateName);
            var parent = model.GetType().GetProperty("ParentTask")
                ?.GetValue(model);
            ParentTaskIds.Add(parent?.GetType().GetProperty("Id")
                ?.GetValue(parent) as string);
            ToolNames.Add(model.GetType().GetProperty("ResultToolName")
                ?.GetValue(model) as string);
            var dependencies = model.GetType()
                .GetProperty("ResolvedDependencies")
                ?.GetValue(model) as IEnumerable<ResolvedArtifactDependency>;
            if (dependencies is not null)
            {
                ResolvedTypeNames.AddRange(dependencies.SelectMany(item =>
                    item.FullyQualifiedTypeNames));
            }
            return Task.FromResult($"rendered:{templateName}");
        }
    }
}
