using FluentAssertions;
using Penghou.Baize;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class ArchitectureGapResolutionPromptBuilderTests
{
    [Fact]
    public async Task BuildAsync_IncludesPracticesAndVersionedDecisionId()
    {
        var builder = new ArchitectureGapResolutionPromptBuilder(
            new ScribanPromptTemplateEngine(
                new FilePromptLoader(Path.Combine(
                    AppContext.BaseDirectory,
                    "prompts"))));
        var practice = new ArchitecturePractice
        {
            Id = "api.problem-details",
            Title = "Standards-based HTTP errors",
            Guidance = "Use Problem Details.",
            Applicability = "HTTP API errors.",
            Reasons = ["Interoperability."],
            Scope = "Established"
        };

        var request = await builder.BuildAsync(
            new ArchitectureGapResolutionPromptContext(
                PlanTestData.Create(),
                ArchitectureReviewValidatorTests.CreateReview(false)
                    .Findings[0],
                [practice],
                "ADR-RESOLUTION-V2-AR-01",
                LlmResponseFormat.JsonSchema("""{"type":"object"}"""),
                4000),
            TestContext.Current.CancellationToken);

        request.Messages[1].Text().Should().Contain("api.problem-details");
        request.Messages[1].Text().Should().Contain(
            "ADR-RESOLUTION-V2-AR-01");
        request.Messages[0].Text().Should().Contain(
            "Reuse an applicable practice");
        request.Messages[0].Text().Should().Contain(
            "Create one complete ADR");
        request.Messages[0].Text().Should().Contain(
            "declared NuGet package IDs only");
        request.Messages[0].Text().Should().Contain(
            "This resolver cannot introduce new packages");
    }
}
