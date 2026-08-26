using FluentAssertions;
using Guyabano.Llm.Prompting;
using Penghou.Baize;

namespace Guyabano.Llm.CodeGeneration.Tests;

public sealed class CodeGenerationPromptBuilderTests
{
    [Fact]
    public async Task BuildAsync_RendersBothPromptsAndAttachesContextTools()
    {
        var templateEngine = new RecordingTemplateEngine();
        var builder = new CodeGenerationPromptBuilder(templateEngine);
        var tool = new LlmTool(
            "emit_files",
            "Emits files.",
            """{"type":"object"}""");

        var request = await builder.BuildAsync(
            new CodeGenerationPromptContext(
                Task: "Generate code.",
                ResultToolName: tool.Name,
                ProjectName: "Example",
                RootNamespace: "Example",
                TargetFramework: "net10.0",
                Tools: [tool]),
            TestContext.Current.CancellationToken);

        request.Messages.Should().HaveCount(2);
        TextOf(request.Messages[0]).Should().Be(
            "rendered:code-generation/system.sbn");
        TextOf(request.Messages[1]).Should().Be(
            "rendered:code-generation/user.sbn");
        request.Tools.Should().ContainSingle().Which.Should().Be(tool);
        templateEngine.TemplateNames.Should().Equal(
            "code-generation/system.sbn",
            "code-generation/user.sbn");
        templateEngine.ResultToolNames.Should().OnlyContain(
            name => name == "emit_files");
        templateEngine.ProjectNames.Should().OnlyContain(
            name => name == "Example");
    }

    [Fact]
    public async Task BuildAsync_RendersSnakeCaseTemplateVariables()
    {
        var loader = new DictionaryPromptLoader(
            new Dictionary<string, string>
            {
                ["code-generation/system.sbn"] =
                    "{{ project_name }}|{{ root_namespace }}|{{ target_framework }}|{{ result_tool_name }}",
                ["code-generation/user.sbn"] =
                    "{{ solution_file }}|{{ application_project_file }}|{{ test_project_file }}"
            });
        var builder = new CodeGenerationPromptBuilder(
            new ScribanPromptTemplateEngine(loader));
        var tool = new LlmTool(
            "emit_files",
            "Emits files.",
            """{"type":"object"}""");

        var request = await builder.BuildAsync(
            new CodeGenerationPromptContext(
                Task: "Generate code.",
                ResultToolName: tool.Name,
                ProjectName: "Example",
                RootNamespace: "Example.Root",
                TargetFramework: "net10.0",
                Tools: [tool]),
            TestContext.Current.CancellationToken);

        TextOf(request.Messages[0]).Should().Be(
            "Example|Example.Root|net10.0|emit_files");
        TextOf(request.Messages[1]).Should().Be(
            "Example.sln|src/Example/Example.csproj|tests/Example.Tests/Example.Tests.csproj");
    }

    private sealed class RecordingTemplateEngine : IPromptTemplateEngine
    {
        public List<string> TemplateNames { get; } = [];

        public List<string?> ResultToolNames { get; } = [];

        public List<string?> ProjectNames { get; } = [];

        public Task<string> RenderAsync(
            string templateName,
            object model,
            CancellationToken cancellationToken = default)
        {
            TemplateNames.Add(templateName);
            ResultToolNames.Add(
                model.GetType()
                    .GetProperty("ResultToolName")
                    ?.GetValue(model) as string);
            ProjectNames.Add(
                model.GetType()
                    .GetProperty("ProjectName")
                    ?.GetValue(model) as string);

            return Task.FromResult($"rendered:{templateName}");
        }
    }

    private sealed class DictionaryPromptLoader(
        IReadOnlyDictionary<string, string> templates)
        : IPromptLoader
    {
        public Task<string> LoadAsync(
            string promptName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(templates[promptName]);
    }

    private static string TextOf(LlmMessage message) =>
        message.Parts.OfType<LlmTextContent>().Single().Text;
}
