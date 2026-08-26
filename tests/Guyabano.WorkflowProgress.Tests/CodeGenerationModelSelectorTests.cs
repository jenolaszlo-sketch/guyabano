using FluentAssertions;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationModelSelectorTests
{
    [Fact]
    public void Select_AllowsPrimaryModelWithoutFallback()
    {
        var options = new CodeGenerationWorkerOptions
        {
            Model = "flash",
            EscalationModels = []
        };

        var selection = CodeGenerationModelSelector.Select(
            options,
            attempt: 2);

        selection.Model.Should().Be("flash");
        selection.Tier.Should().Be(1);
        selection.ModelAttempt.Should().Be(2);
        CodeGenerationModelSelector.MaximumAttempts(options, 1)
            .Should().Be(2);
    }

    [Fact]
    public void HasValidConfiguration_RejectsDuplicateFallback()
    {
        var options = new CodeGenerationWorkerOptions
        {
            Model = "flash",
            EscalationModels = ["FLASH"]
        };

        CodeGenerationModelSelector.HasValidConfiguration(options)
            .Should().BeFalse();
    }

    [Fact]
    public void Select_ClampsBuildRepairToLastConfiguredTier()
    {
        var options = new CodeGenerationWorkerOptions
        {
            Model = "flash",
            EscalationModels = []
        };

        var selection = CodeGenerationModelSelector.Select(
            options,
            attempt: 1,
            startingTier: 2);

        selection.Model.Should().Be("flash");
        selection.Tier.Should().Be(1);
        CodeGenerationModelSelector.MaximumAttempts(options, 2)
            .Should().Be(2);
    }

    [Theory]
    [InlineData(1, "small", 1, 1)]
    [InlineData(2, "small", 1, 2)]
    [InlineData(3, "large", 2, 1)]
    [InlineData(4, "large", 2, 2)]
    public void Select_UsesTwoAttemptsAtEachModelTier(
        int attempt,
        string expectedModel,
        int expectedTier,
        int expectedModelAttempt)
    {
        var options = new CodeGenerationWorkerOptions
        {
            Model = "small",
            EscalationModels = ["large"]
        };

        var selection = CodeGenerationModelSelector.Select(
            options,
            attempt);

        selection.Model.Should().Be(expectedModel);
        selection.Tier.Should().Be(expectedTier);
        selection.ModelAttempt.Should().Be(expectedModelAttempt);
    }

    [Theory]
    [InlineData(1, "large", 2, 1)]
    [InlineData(2, "large", 2, 2)]
    public void Select_CanStartAtAnEscalatedTier(
        int attempt,
        string expectedModel,
        int expectedTier,
        int expectedModelAttempt)
    {
        var options = new CodeGenerationWorkerOptions
        {
            Model = "small",
            EscalationModels = ["large"]
        };

        var selection = CodeGenerationModelSelector.Select(
            options,
            attempt,
            startingTier: 2);

        selection.Model.Should().Be(expectedModel);
        selection.Tier.Should().Be(expectedTier);
        selection.ModelAttempt.Should().Be(expectedModelAttempt);
    }
}
