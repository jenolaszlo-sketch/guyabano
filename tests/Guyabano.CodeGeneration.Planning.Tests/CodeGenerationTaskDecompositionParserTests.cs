using System.Text.Json;
using FluentAssertions;
using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class CodeGenerationTaskDecompositionParserTests
{
    [Fact]
    public void Parse_AcceptsCompleteStructuredDecomposition()
    {
        var expected =
            CodeGenerationTaskDecompositionValidatorTests.CreateReady(
                "TASK-001");
        var response = new LlmResponse(
            Content: string.Empty,
            ToolCalls:
            [
                new LlmToolCall(
                    "call-1",
                    "return_task_decomposition",
                    JsonSerializer.Serialize(expected))
            ]);

        var result = new CodeGenerationTaskDecompositionParser()
            .Parse(response);

        result.Succeeded.Should().BeTrue(result.Error);
        result.Value!.Status.Should().Be(TaskDecompositionStatus.Ready);
        result.Value.LeafTasks.Should().ContainSingle();
        result.Value.LeafTasks[0].Artifacts.Should().ContainSingle();
    }

    [Fact]
    public void Parse_RejectsMissingLeafShape()
    {
        var response = new LlmResponse(
            Content: string.Empty,
            ToolCalls:
            [
                new LlmToolCall(
                    "call-1",
                    "return_task_decomposition",
                    """{"parentTaskId":"TASK-001","status":"Ready"}""")
            ]);

        var result = new CodeGenerationTaskDecompositionParser()
            .Parse(response);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(
            ToolCallParseFailure.SchemaValidationFailed);
    }
}
