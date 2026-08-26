using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class DecompositionModelSelectorTests
{
    [Fact]
    public void Options_DefaultFallbackCollectionIsEmptySoBindingDoesNotAppendToIt()
    {
        var options = new CodeGenerationWorkerOptions();

        options.DecompositionEscalationModels.Should().BeEmpty();
        DecompositionModelSelector.HasValidConfiguration(options)
            .Should().BeTrue();
    }

    [Fact]
    public void OptionsBinding_AddsConfiguredFallbackExactlyOnce()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGeneration:DecompositionEscalationModels:0"] =
                    "pro"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<CodeGenerationWorkerOptions>()
            .Bind(configuration.GetSection("CodeGeneration"));

        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<IOptions<CodeGenerationWorkerOptions>>()
            .Value;

        options.DecompositionEscalationModels
            .Should().Equal("pro");
    }

    [Theory]
    [InlineData(1, "flash", 1, 1)]
    [InlineData(2, "flash", 1, 2)]
    [InlineData(3, "pro", 2, 1)]
    [InlineData(4, "pro", 2, 2)]
    public void Select_UsesTwoAttemptsBeforeEscalating(
        int attempt,
        string expectedModel,
        int expectedTier,
        int expectedModelAttempt)
    {
        var options = new CodeGenerationWorkerOptions
        {
            DecompositionModel = "flash",
            DecompositionEscalationModels = ["pro"]
        };

        var selection = DecompositionModelSelector.Select(
            options,
            attempt);

        selection.Model.Should().Be(expectedModel);
        selection.Tier.Should().Be(expectedTier);
        selection.ModelAttempt.Should().Be(expectedModelAttempt);
    }

    [Fact]
    public void Select_UsesOnlyPrimaryModelWhenFallbackIsNotBound()
    {
        var options = new CodeGenerationWorkerOptions
        {
            DecompositionModel = "flash",
            DecompositionEscalationModels = [],
            ArchitectureReviewModel = "pro"
        };

        var selection = DecompositionModelSelector.Select(
            options,
            attempt: 2);

        selection.Model.Should().Be("flash");
        selection.Tier.Should().Be(1);
        selection.ModelAttempt.Should().Be(2);
        DecompositionModelSelector.MaximumAttempts(options).Should().Be(2);
    }

    [Fact]
    public void Select_RejectsAttemptBeyondConfiguredChain()
    {
        var options = new CodeGenerationWorkerOptions
        {
            DecompositionModel = "flash",
            DecompositionEscalationModels = []
        };

        var action = () => DecompositionModelSelector.Select(
            options,
            attempt: 3);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }
}
