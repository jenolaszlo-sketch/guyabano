using FluentAssertions;
using Guyabano.Llm.Prompting;
using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class ArchitectureReviewPromptBuilderTests
{
    [Fact]
    public async Task BuildAsync_IncludesRejectedReviewFailureOnModelRetry()
    {
        var builder = new ArchitectureReviewPromptBuilder(
            new ScribanPromptTemplateEngine(
                new FilePromptLoader(Path.Combine(
                    AppContext.BaseDirectory,
                    "prompts"))));
        var format = LlmResponseFormat.JsonSchema(
            """{"type":"object"}""");
        const string previousFailure =
            "Review finding F-003 is semantically inconsistent.";

        var request = await builder.BuildAsync(
            new ArchitectureReviewPromptContext(
                PlanTestData.Create(),
                2,
                format)
            {
                PreviousFailure = previousFailure
            },
            TestContext.Current.CancellationToken);

        request.Messages[1].Text().Should().Contain(previousFailure);
        request.Messages[1].Text().Should().Contain(
            "Correct that exact structural or semantic failure");
        request.Tools.Should().BeEmpty();
        request.ResponseFormat.Should().Be(format);
    }

    [Fact]
    public async Task BuildAsync_UsesPreviousReviewAsConvergenceScope()
    {
        var builder = new ArchitectureReviewPromptBuilder(
            new ScribanPromptTemplateEngine(
                new FilePromptLoader(Path.Combine(
                    AppContext.BaseDirectory,
                    "prompts"))));
        var previousReview = ArchitectureReviewValidatorTests.CreateReview(
            approved: false);

        var request = await builder.BuildAsync(
            new ArchitectureReviewPromptContext(
                PlanTestData.Create(),
                2,
                LlmResponseFormat.JsonSchema("""{"type":"object"}"""),
                PreviousReview: previousReview),
            TestContext.Current.CancellationToken);

        request.Messages[1].Text().Should().Contain(
            "This is a convergence review");
        request.Messages[1].Text().Should().Contain("AR-01");
        request.Messages[1].Text().Should().Contain(
            "Do not restart a broad architecture audit");
    }

    [Fact]
    public async Task BuildAsync_IncludesExplicitlySelectedSessionContext()
    {
        var builder = new ArchitectureReviewPromptBuilder(
            new ScribanPromptTemplateEngine(
                new FilePromptLoader(Path.Combine(
                    AppContext.BaseDirectory,
                    "prompts"))));
        using var disclosure = SessionContextDisclosureScope.Push(
            "Cangjie decision and Hetu dependency edge");

        var request = await builder.BuildAsync(
            new ArchitectureReviewPromptContext(
                PlanTestData.Create(),
                1,
                LlmResponseFormat.JsonSchema("""{"type":"object"}""")),
            TestContext.Current.CancellationToken);

        request.Messages[1].Text().Should().Contain(
            "Cangjie decision and Hetu dependency edge");
    }
}
