using FluentAssertions;
using Guyabano.CodeGeneration.Planning.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class TopologicalContextsTests
{
    [Fact]
    public void Order_returns_independent_contexts_in_ordinal_order()
    {
        var ordered = TopologicalContexts.Order([Context("Notes"), Context("Todos")]);

        ordered.Select(context => context.Name).Should().Equal("Notes", "Todos");
    }

    [Fact]
    public void Order_places_dependencies_before_dependents()
    {
        var ordered = TopologicalContexts.Order([
            Context("Notes", "Todos"),
            Context("Todos"),
            Context("Search", "Notes"),
        ]);

        ordered.Select(context => context.Name).Should().Equal("Todos", "Notes", "Search");
    }

    [Fact]
    public void Order_rejects_cycles()
    {
        var act = () => TopologicalContexts.Order([
            Context("A", "B"),
            Context("B", "A"),
        ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*cycle*");
    }

    [Fact]
    public void Order_rejects_unknown_dependencies()
    {
        var act = () => TopologicalContexts.Order([Context("A", "Missing")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown dependency*");
    }

    private static BoundedContextPlan Context(string name, params string[] dependsOn) => new()
    {
        Name = name,
        Purpose = "test",
        CapabilityNames = [],
        DependsOnContextNames = [.. dependsOn],
        InboundAdapters = [],
        OutboundAdapters = [],
    };
}
