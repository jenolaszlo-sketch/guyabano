using FluentAssertions;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class DefaultArchitecturePracticeProviderTests
{
    [Fact]
    public void GetPractices_ReturnsUniqueCompleteEstablishedPractices()
    {
        var practices = new DefaultArchitecturePracticeProvider()
            .GetPractices();

        practices.Should().NotBeEmpty();
        practices.Select(item => item.Id).Should().OnlyHaveUniqueItems();
        practices.Should().OnlyContain(item =>
            item.Scope == "Established" &&
            !string.IsNullOrWhiteSpace(item.Title) &&
            !string.IsNullOrWhiteSpace(item.Guidance) &&
            !string.IsNullOrWhiteSpace(item.Applicability) &&
            item.Reasons.Count > 0);
    }
}
