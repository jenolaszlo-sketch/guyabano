using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class CodeGenerationPlanParserTests
{
    [Fact]
    public void Parse_AcceptsCompleteStructuredPlan()
    {
        var expected = PlanTestData.Create();
        var response = new LlmResponse(
            Content: JsonSerializer.Serialize(expected));

        var result = new CodeGenerationPlanParser().Parse(response);

        result.Succeeded.Should().BeTrue(result.Error);
        result.Value!.Tasks.Should().ContainSingle();
        result.Value.Tasks[0].ComplexityPoints.Should().Be(3);
        result.Value.Tasks[0].ExecutionKind.Should().Be(
            PlanTaskExecutionKind.CodeGeneration);
        result.Value.Solution.Path.Should().Be("Todo.sln");
        result.Value.AcceptanceCriteria.Should().ContainSingle();
    }

    [Fact]
    public void Parse_RejectsPlanMissingRequiredCollections()
    {
        var response = new LlmResponse(
            Content:
                """{"title":"Incomplete","summary":"Missing fields"}""");

        var result = new CodeGenerationPlanParser().Parse(response);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(
            ToolCallParseFailure.SchemaValidationFailed);
    }

    [Fact]
    public void Parse_ReportsEmptyStructuredContentAsEmptyArguments()
    {
        var result = new CodeGenerationPlanParser().Parse(
            new LlmResponse(Content: string.Empty));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(ToolCallParseFailure.EmptyArguments);
        result.Error.Should().Contain("arguments were empty");
    }

    [Fact]
    public void Parse_PreservesLengthLimitedFinishReason()
    {
        var result = new CodeGenerationPlanParser().Parse(
            new LlmResponse(
                Content: "{\"title\":\"Incomplete",
                FinishReason: "max_tokens"));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(
            ToolCallParseFailure.TruncatedResponse);
        result.Error.Should().Contain("output token limit");
    }
}
