using System.Text.Json;
using FluentAssertions;
using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class ArchitectureReviewParserTests
{
    [Fact]
    public void Parse_AcceptsStructuredReview()
    {
        var review = ArchitectureReviewValidatorTests.CreateReview(false);
        var response = new LlmResponse(
            Content: JsonSerializer.Serialize(review));

        var result = new ArchitectureReviewParser().Parse(response);

        result.Succeeded.Should().BeTrue(result.Error);
        result.Value!.Findings.Should().ContainSingle();
        result.Value.Findings[0].Severity.Should().Be(
            ArchitectureReviewSeverity.Blocking);
    }

    [Fact]
    public void Parse_ReportsEmptyStructuredContentAsEmptyArguments()
    {
        var result = new ArchitectureReviewParser().Parse(
            new LlmResponse(Content: string.Empty));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(ToolCallParseFailure.EmptyArguments);
    }

    [Fact]
    public void Parse_PreservesTruncationAndRepairDiagnostics()
    {
        var response = new LlmResponse(
            Content: "{\"approved\":false,\"findings\":[{\"id\":\"F-1",
            FinishReason: "length",
            ContentRepairAttempts:
            [
                new LlmRepairAttempt(
                    "content/tolerant-syntax-tree",
                    LlmRepairStatus.Succeeded)
            ])
        {
            ContentRepairDiagnostics = new LlmJsonRepairDiagnostics(
                LlmRepairShapeStatus.Mismatched,
                ["$.findings[0].requiresUserInput is required."])
        };

        var result = new ArchitectureReviewParser().Parse(response);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(
            ToolCallParseFailure.TruncatedResponse);
        result.Error.Should().Contain("output token limit");
        result.Error.Should().Contain("repair was attempted");
        result.Error.Should().Contain("requiresUserInput is required");
    }
}
