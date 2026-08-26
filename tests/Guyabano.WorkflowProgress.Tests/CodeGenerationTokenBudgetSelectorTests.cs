using FluentAssertions;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationTokenBudgetSelectorTests
{
    [Fact]
    public void Select_UsesModelSpecificInitialAllowance()
    {
        var options = CreateOptions();

        CodeGenerationTokenBudgetSelector.Select(
                options,
                "deepseek-v4-flash",
                attempt: 1)
            .Should()
            .Be(16000);
    }

    [Fact]
    public void Select_UsesLargerModelSpecificRetryAllowance()
    {
        var options = CreateOptions();

        CodeGenerationTokenBudgetSelector.Select(
                options,
                "deepseek-v4-flash",
                attempt: 2)
            .Should()
            .Be(32000);
    }

    [Theory]
    [InlineData(1, 8000)]
    [InlineData(2, 16000)]
    public void Select_UsesDefaultsForUnconfiguredModel(
        int attempt,
        int expected)
    {
        var options = CreateOptions();

        CodeGenerationTokenBudgetSelector.Select(
                options,
                "qwen2.5-coder:7b",
                attempt)
            .Should()
            .Be(expected);
    }

    [Fact]
    public void Select_RejectsNonPositiveAllowance()
    {
        var options = CreateOptions();
        options.ModelTokenBudgets["broken-model"].MaxTokens = 0;

        var action = () => CodeGenerationTokenBudgetSelector.Select(
            options,
            "broken-model",
            attempt: 1);

        action.Should().Throw<InvalidOperationException>();
    }

    private static CodeGenerationWorkerOptions CreateOptions() =>
        new()
        {
            DefaultMaxTokens = 8000,
            DefaultRetryMaxTokens = 16000,
            ModelTokenBudgets = new Dictionary<
                string,
                CodeGenerationTokenBudgetOptions>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["deepseek-v4-flash"] = new()
                {
                    MaxTokens = 16000,
                    RetryMaxTokens = 32000
                },
                ["broken-model"] = new()
                {
                    MaxTokens = 8000,
                    RetryMaxTokens = 16000
                }
            }
        };
}
