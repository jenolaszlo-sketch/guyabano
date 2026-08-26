using FluentAssertions;
using Guyabano.Llm.Prompting;
using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class ArchitectureDecisionIntegrationPromptBuilderTests
{
    [Fact]
    public async Task BuildAsync_IncludesPreviousValidationFailureOnRetry()
    {
        var builder = new ArchitectureDecisionIntegrationPromptBuilder(
            new ScribanPromptTemplateEngine(
                new FilePromptLoader(Path.Combine(
                    AppContext.BaseDirectory,
                    "prompts"))));
        var tool = new LlmTool(
            "return_architecture_decision_patch",
            "Returns a decision patch.",
            """{"type":"object"}""");
        const string previousFailure =
            "Architecture replacement 'TodoApi.IntegrationTests' does not exist.";

        var request = await builder.BuildAsync(
            new ArchitectureDecisionIntegrationPromptContext(
                PlanTestData.Create(),
                ArchitectureReviewValidatorTests.CreateReview(false),
                [],
                previousFailure,
                tool.Name,
                [tool]),
            TestContext.Current.CancellationToken);

        request.Messages[1].Text().Should().Contain(previousFailure);
        request.Messages[1].Text().Should().Contain(
            "Do not reconsider any resolution");
        request.Messages[0].Text().Should().Contain(
            "Every replacement ID must exactly match");
        request.Messages[0].Text().Should().Contain(
            "Treat each finding's suggestedResolution as authoritative");
    }
}
