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

    [Fact]
    public async Task BuildAsync_InheritsWorkflowCorrelationFromActivityScope()
    {
        var builder = new CodeGenerationPlanningPromptBuilder(
            new RecordingTemplateEngine());
        using var scope = LlmRequestCorrelationScope.Push(new(
            "01991c80-3796-7f03-9074-e87e17778ed0",
            "01991c80-8e8a-765c-a037-f855422143c6",
            "architecture-review/1/1"));

        var request = await builder.BuildAsync(
            new CodeGenerationPlanningPromptContext(
                "Build a todo API.",
                LlmResponseFormat.JsonSchema("""{"type":"object"}""")),
            TestContext.Current.CancellationToken);

        request.Metadata.Should().Contain(new Dictionary<string, object?>
        {
            ["guyabano.session_id"] =
                "01991c80-3796-7f03-9074-e87e17778ed0",
            ["guyabano.workflow_run_id"] =
                "01991c80-8e8a-765c-a037-f855422143c6",
            ["guyabano.workflow_step_key"] =
                "architecture-review/1/1"
        });
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
