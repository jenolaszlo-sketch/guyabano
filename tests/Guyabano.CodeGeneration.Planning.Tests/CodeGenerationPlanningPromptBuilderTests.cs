using FluentAssertions;
using Guyabano.Llm.Prompting;
using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class CodeGenerationPlanningPromptBuilderTests
{
    [Fact]
    public async Task BuildAsync_AttachesPlanSchemaAndRendersRequest()
    {
        var engine = new RecordingTemplateEngine();
        var builder = new CodeGenerationPlanningPromptBuilder(engine);
        var format = LlmResponseFormat.JsonSchema(
            """{"type":"object"}""");

        var request = await builder.BuildAsync(
            new CodeGenerationPlanningPromptContext(
                "Build a todo API.",
                format),
            TestContext.Current.CancellationToken);

        request.Tools.Should().BeEmpty();
        request.ResponseFormat.Should().Be(format);
        engine.TemplateNames.Should().Equal(
            "code-generation-planning/system.sbn",
            "code-generation-planning/user.sbn");
        engine.Requests.Should().OnlyContain(
            value => value == "Build a todo API.");
    }

    private sealed class RecordingTemplateEngine : IPromptTemplateEngine
    {
        public List<string> TemplateNames { get; } = [];
        public List<string?> Requests { get; } = [];

        public Task<string> RenderAsync(
            string templateName,
            object model,
            CancellationToken cancellationToken = default)
        {
            TemplateNames.Add(templateName);
            Requests.Add(model.GetType().GetProperty("Request")
                ?.GetValue(model) as string);
            return Task.FromResult($"rendered:{templateName}");
        }
    }
}
