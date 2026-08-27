using FluentAssertions;
using Guyabano.Llm.Prompting;
using Penghou.Baize;

namespace Guyabano.Llm.CodeGeneration.Tests;

public sealed class CodeGenerationTaskPromptBuilderTests
{
    [Fact]
    public async Task BuildAsync_RendersTaskTemplatesAndStructuredContext()
    {
        var loader = new DictionaryPromptLoader(
            new Dictionary<string, string>
            {
                ["code-generation-task/system.sbn"] =
                    "{{ task_id }}|{{ project_directory }}|{{ result_tool_name }}",
                ["code-generation-task/user.sbn"] =
                    "{{ project_name }}|{{ contracts[0].Name }}|{{ acceptance_criteria[0].Id }}"
            });
        var builder = new CodeGenerationTaskPromptBuilder(
            new ScribanPromptTemplateEngine(loader));
        var tool = new LlmTool(
            "emit_task_files",
            "Emits task files.",
            "{\"type\":\"object\"}");
        var context = CreateContext() with
        {
            SessionId = "session-1",
            WorkflowRunId = "workflow-1",
            WorkflowStepKey = "generation/T1"
        };

        var request = await builder.BuildAsync(
            new CodeGenerationTaskPromptContext(
                context,
                tool.Name,
                [tool]),
            TestContext.Current.CancellationToken);

        TextOf(request.Messages[0]).Should().Be(
            "T1|src/Todo.Api|emit_task_files");
        TextOf(request.Messages[1]).Should().Be(
            "Todo.Api|ITodoService|AC1");
        request.Tools.Should().ContainSingle().Which.Should().Be(tool);
        request.Metadata["guyabano.session_id"].Should().Be("session-1");
        request.Metadata["guyabano.workflow_run_id"].Should().Be("workflow-1");
        request.Metadata["guyabano.workflow_step_key"].Should().Be(
            "generation/T1");
        request.Metadata["guyabano.task_id"].Should().Be("T1");
    }

    [Fact]
    public async Task BuildAsync_RendersPreviousAttemptDiagnostics()
    {
        var loader = new DictionaryPromptLoader(
            new Dictionary<string, string>
            {
                ["code-generation-task/system.sbn"] = "system",
                ["code-generation-task/user.sbn"] =
                    "{{ retry.PreviousModel }}|{{ retry.Failure }}|{{ retry.Diagnostics[0] }}"
            });
        var builder = new CodeGenerationTaskPromptBuilder(
            new ScribanPromptTemplateEngine(loader));
        var tool = new LlmTool(
            "emit_task_files",
            "Emits task files.",
            "{\"type\":\"object\"}");
        var context = CreateContext() with
        {
            Retry = new CodeGenerationTaskRetryContext(
                1,
                "small-model",
                "MissingToolCall",
                "No tool call found.",
                ["Markdown fence was removed."],
                [])
        };

        var request = await builder.BuildAsync(
            new CodeGenerationTaskPromptContext(
                context,
                tool.Name,
                [tool]),
            TestContext.Current.CancellationToken);

        TextOf(request.Messages[1]).Should().Be(
            "small-model|MissingToolCall|Markdown fence was removed.");
    }

    [Fact]
    public async Task BuildAsync_ProvidesArchitectureNotesToTemplate()
    {
        var loader = new DictionaryPromptLoader(
            new Dictionary<string, string>
            {
                ["code-generation-task/system.sbn"] = "system",
                ["code-generation-task/user.sbn"] =
                    "{{ architecture_notes[0].Decision }}"
            });
        var builder = new CodeGenerationTaskPromptBuilder(
            new ScribanPromptTemplateEngine(loader));
        var tool = new LlmTool(
            "emit_task_files",
            "Emits task files.",
            "{\"type\":\"object\"}");
        var context = CreateContext() with
        {
            ArchitectureNotes =
            [
                new CodeGenerationTaskArchitectureNoteContext(
                    "NOTE-TITLE-LENGTH",
                    "InferredDomainConstraint",
                    "Todo title length",
                    "Limit todo titles to 200 characters.",
                    "Long titles are rejected.",
                    ["Titles should remain concise."])
            ]
        };

        var request = await builder.BuildAsync(
            new CodeGenerationTaskPromptContext(
                context,
                tool.Name,
                [tool]),
            TestContext.Current.CancellationToken);

        TextOf(request.Messages[1]).Should().Contain(
            "Limit todo titles to 200 characters");
    }

    [Fact]
    public async Task BuildAsync_ExposesBuildArtifactPermissionToTemplate()
    {
        var loader = new DictionaryPromptLoader(
            new Dictionary<string, string>
            {
                ["code-generation-task/system.sbn"] =
                    "{{ allow_build_artifacts }}",
                ["code-generation-task/user.sbn"] = "repair"
            });
        var builder = new CodeGenerationTaskPromptBuilder(
            new ScribanPromptTemplateEngine(loader));
        var tool = new LlmTool(
            "emit_task_files",
            "Emits task files.",
            "{\"type\":\"object\"}");
        var context = CreateContext() with
        {
            AllowBuildArtifacts = true
        };

        var request = await builder.BuildAsync(
            new CodeGenerationTaskPromptContext(
                context,
                tool.Name,
                [tool]),
            TestContext.Current.CancellationToken);

        TextOf(request.Messages[0]).Should().Be("true");
    }

    private static CodeGenerationTaskContext CreateContext() =>
        new(
            "Build a todo API.",
            "T1",
            "Implement service",
            "Implement todo behavior.",
            "Todo",
            "Todo.sln",
            "Todo.Api",
            "src/Todo.Api/Todo.Api.csproj",
            "src/Todo.Api",
            "Todo.Api",
            "net10.0",
            "Services",
            ["Implement business rules."],
            ["TodoService.cs"],
            [
                new CodeGenerationTaskContractContext(
                    "C1",
                    "ITodoService",
                    "Interface",
                    "Todo operations.",
                    ["Todo Create(string title)"])
            ],
            [
                new CodeGenerationTaskAcceptanceContext(
                    "AC1",
                    "Create todo",
                    "Valid title",
                    ["a title"],
                    ["the todo is created"],
                    ["the title is trimmed"])
            ],
            [],
            []);

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
