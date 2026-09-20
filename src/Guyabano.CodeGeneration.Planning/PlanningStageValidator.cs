namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Deterministic admission for planner-proposed stage plans: known stages,
/// version match, acyclic instance graph, resolvable inputs, and kind flow.
/// Models propose; this validator owns admission.
/// </summary>
public static class PlanningStageValidator
{
    /// <returns>Error descriptions; empty when the plan is admissible.</returns>
    public static IReadOnlyList<string> Validate(
        PlanningStagePlan plan,
        PlanningStageCatalogue catalogue,
        IReadOnlySet<string> knownRevisions)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(knownRevisions);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(plan.Rationale))
        {
            errors.Add("Stage plan must state a rationale.");
        }

        if (plan.Provenance is null)
        {
            errors.Add("Stage plan must carry provenance.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(plan.Provenance.ProducedBy))
        {
            errors.Add("Stage plan provenance must name its producer.");
        }

        if (!string.Equals(
                plan.Provenance.DefinitionCatalogueVersion,
                catalogue.Version,
                StringComparison.Ordinal))
        {
            errors.Add(
                "Stage plan was built against a different definition catalogue and is stale.");
            return errors;
        }

        foreach (var revision in plan.Provenance.InputRevisions
                     .Where(revision => !knownRevisions.Contains(revision))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(revision => revision, StringComparer.Ordinal))
        {
            errors.Add($"Stage plan provenance observes unknown artifact revision '{revision}'.");
        }

        var definitions = catalogue.Definitions
            .ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        var instances = new Dictionary<string, PlannedStage>(StringComparer.Ordinal);
        foreach (var stage in plan.Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Name))
            {
                errors.Add("Stage instances must be named.");
                continue;
            }

            if (!instances.TryAdd(stage.Name, stage))
            {
                errors.Add($"Stage plan contains duplicate instance '{stage.Name}'.");
            }
        }

        foreach (var stage in plan.Stages)
        {
            if (!definitions.TryGetValue(stage.StageId, out var definition))
            {
                errors.Add(
                    $"Stage instance '{stage.Name}' references unknown stage id '{stage.StageId}'.");
                continue;
            }

            var kind = ArtifactKind(stage.Name);
            if (kind is null)
            {
                errors.Add(
                    $"Stage instance '{stage.Name}' is not a kind/name identity.");
            }
            else if (!string.Equals(kind, definition.ArtifactKind, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Stage instance '{stage.Name}' has kind '{kind}' but definition " +
                    $"'{definition.Id}' produces '{definition.ArtifactKind}'.");
            }

            foreach (var dependency in stage.DependsOn)
            {
                if (!instances.ContainsKey(dependency))
                {
                    errors.Add(
                        $"Stage instance '{stage.Name}' depends on unknown instance '{dependency}'.");
                }
                else if (string.Equals(stage.Name, dependency, StringComparison.Ordinal))
                {
                    errors.Add($"Stage instance '{stage.Name}' cannot depend on itself.");
                }
                else if (definitions.TryGetValue(instances[dependency].StageId, out var upstream) &&
                         !definition.InputKinds.Contains(upstream.ArtifactKind, StringComparer.Ordinal))
                {
                    errors.Add(
                        $"Stage instance '{stage.Name}' consumes '{upstream.ArtifactKind}' " +
                        $"which definition '{definition.Id}' does not declare.");
                }
            }

            foreach (var input in stage.InputArtifacts)
            {
                var inputKind = ArtifactKind(WithoutRevision(input));
                if (inputKind is null || !knownRevisions.Contains(input))
                {
                    errors.Add(
                        $"Stage instance '{stage.Name}' consumes unknown artifact revision '{input}'.");
                }
                else if (!definition.InputKinds.Contains(inputKind, StringComparer.Ordinal))
                {
                    errors.Add(
                        $"Stage instance '{stage.Name}' consumes kind '{inputKind}' " +
                        $"which definition '{definition.Id}' does not declare.");
                }
            }
        }

        if (HasCycle(plan))
        {
            errors.Add("Stage plan contains a dependency cycle.");
        }

        return errors;
    }

    /// <summary>Topological instance order; empty when the graph has a cycle.</summary>
    public static IReadOnlyList<PlannedStage> Order(IReadOnlyList<PlannedStage> stages)
    {
        var byName = stages.ToDictionary(stage => stage.Name, StringComparer.Ordinal);
        var indegree = stages.ToDictionary(stage => stage.Name, _ => 0, StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            foreach (var dependency in stage.DependsOn.Distinct(StringComparer.Ordinal))
            {
                if (byName.ContainsKey(stage.Name) && byName.ContainsKey(dependency))
                {
                    indegree[stage.Name]++;
                }
            }
        }

        var queue = new Queue<string>(indegree
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key));
        var ordered = new List<PlannedStage>();
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            ordered.Add(byName[name]);
            foreach (var stage in stages)
            {
                if (stage.DependsOn.Contains(name, StringComparer.Ordinal) &&
                    --indegree[stage.Name] == 0)
                {
                    queue.Enqueue(stage.Name);
                }
            }
        }

        return ordered.Count == stages.Count ? ordered : [];
    }

    private static bool HasCycle(PlanningStagePlan plan) =>
        plan.Stages.Count > 0 && Order(plan.Stages).Count == 0;

    private static string WithoutRevision(string version)
    {
        var at = version.LastIndexOf('@');
        return at > 0 ? version[..at] : version;
    }

    private static string? ArtifactKind(string identity)
    {
        var slash = identity.IndexOf('/');
        return slash > 0 ? identity[..slash] : null;
    }
}
