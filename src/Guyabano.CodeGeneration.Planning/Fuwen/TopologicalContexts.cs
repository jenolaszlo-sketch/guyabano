namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Topological ordering of bounded contexts by dependency, mirroring the
/// staged service's context scheduling so phased execution honors
/// upstream-before-downstream without duplicating its logic.
/// </summary>
public static class TopologicalContexts
{
    /// <summary>
    /// Orders contexts so every context follows its dependencies
    /// (ordinal tiebreak for determinism).
    /// </summary>
    public static IReadOnlyList<BoundedContextPlan> Order(
        IEnumerable<BoundedContextPlan> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        var byName = contexts.ToDictionary(
            item => item.Name, StringComparer.Ordinal);
        var remaining = byName.ToDictionary(
            item => item.Key,
            item => item.Value.DependsOnContextNames.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var dependencies in remaining.Values)
        {
            foreach (var name in dependencies)
            {
                if (!byName.ContainsKey(name))
                    throw new InvalidOperationException(
                        $"Bounded context references unknown dependency '{name}'.");
            }
        }
        var result = new List<BoundedContextPlan>();
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(item => item.Value.Count == 0)
                .Select(item => item.Key)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
                throw new InvalidOperationException(
                    "The bounded-context dependency graph contains a cycle.");
            foreach (var name in ready)
            {
                result.Add(byName[name]);
                remaining.Remove(name);
                foreach (var dependencies in remaining.Values)
                    dependencies.Remove(name);
            }
        }
        return result;
    }
}
