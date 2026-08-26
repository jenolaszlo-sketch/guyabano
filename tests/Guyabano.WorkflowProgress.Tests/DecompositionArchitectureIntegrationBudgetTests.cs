using FluentAssertions;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowProgressTests;

public sealed class DecompositionArchitectureIntegrationBudgetTests
{
    [Fact]
    public void Budget_IsIndependentForEachDecompositionTarget()
    {
        var budget = new DecompositionArchitectureIntegrationBudget(2);

        budget.TryConsume("T-05", out var firstT05).Should().BeTrue();
        budget.TryConsume("T-05", out var secondT05).Should().BeTrue();
        budget.TryConsume("T-07", out var firstT07).Should().BeTrue();

        firstT05.Should().Be(1);
        secondT05.Should().Be(2);
        firstT07.Should().Be(1);
    }

    [Fact]
    public void Budget_StopsRepeatedGapForSameTarget()
    {
        var budget = new DecompositionArchitectureIntegrationBudget(2);

        budget.TryConsume("T-07", out _).Should().BeTrue();
        budget.TryConsume("T-07", out _).Should().BeTrue();
        budget.TryConsume("T-07", out var rejectedAttempt).Should().BeFalse();

        rejectedAttempt.Should().Be(3);
    }
}
