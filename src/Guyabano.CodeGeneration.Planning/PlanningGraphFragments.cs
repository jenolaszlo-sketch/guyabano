using System.Text.Json;
using Guyabano.Artifacts;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>One versioned artifact with its payload for graph derivation.</summary>
public sealed record FragmentArtifact(PlanningArtifactVersion Version, JsonElement Payload);

/// <summary>What one artifact kind contributes: versions, payloads, and the resolved descriptor.</summary>
public sealed record PlanningGraphFragmentInput(
    string ArtifactKind,
    IReadOnlyList<FragmentArtifact> Artifacts,
    DescriptorReference Descriptor);

/// <summary>Steps plus their bindings emitted for one artifact kind.</summary>
public sealed record PlanningGraphFragment(
    IReadOnlyList<PlannedExecutionStep> Steps,
    IReadOnlyList<PlannedNodeBinding> Bindings);

/// <summary>
/// Derives execution structure from one artifact kind. Kinds without a
/// registered builder contribute nothing; the planner then shapes the graph
/// explicitly through workflow patches.
/// </summary>
public interface IPlanningGraphFragmentBuilder
{
    /// <summary>Artifact kind this builder derives from.</summary>
    string ArtifactKind { get; }

    PlanningGraphFragment BuildFragment(PlanningGraphFragmentInput input);
}

/// <summary>One assembly run: the merged design plus skipped kinds.</summary>
public sealed record PlanningGraphAssemblyResult(
    PlannedExecutionDesign Design,
    IReadOnlyList<string> SkippedKinds);

/// <summary>
/// Concatenates per-kind fragments into one execution design in input
/// order. Dependencies may cross fragment boundaries and are validated
/// globally, along with id uniqueness and binding coverage.
/// </summary>
public static class PlanningGraphFragmentAssembler
{
    /// <summary>Assembles the design; unknown kinds are skipped for explicit patching.</summary>
    public static PlanningGraphAssemblyResult Assemble(
        string workflowName,
        string inputType,
        string outputType,
        IReadOnlyList<PlanningGraphFragmentInput> inputs,
        IReadOnlyDictionary<string, IPlanningGraphFragmentBuilder> builders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputType);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputType);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(builders);

        var steps = new List<PlannedExecutionStep>();
        var bindings = new List<PlannedNodeBinding>();
        var skipped = new List<string>();
        foreach (var input in inputs)
        {
            if (!builders.TryGetValue(input.ArtifactKind, out var builder))
            {
                if (!skipped.Contains(input.ArtifactKind, StringComparer.Ordinal))
                {
                    skipped.Add(input.ArtifactKind);
                }

                continue;
            }

            var fragment = builder.BuildFragment(input);
            ArgumentNullException.ThrowIfNull(fragment);
            steps.AddRange(fragment.Steps);
            bindings.AddRange(fragment.Bindings);
        }

        var errors = Validate(steps, bindings);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Graph assembly failed: " + string.Join(" ", errors));
        }

        return new PlanningGraphAssemblyResult(
            new PlannedExecutionDesign(
                new PlannedExecutionGraph
                {
                    WorkflowName = workflowName,
                    InputType = inputType,
                    OutputType = outputType,
                    Steps = steps,
                },
                new PlannedExecutionBindings
                {
                    WorkflowName = workflowName,
                    Nodes = bindings,
                }),
            skipped);
    }

    private static IReadOnlyList<string> Validate(
        IReadOnlyList<PlannedExecutionStep> steps,
        IReadOnlyList<PlannedNodeBinding> bindings)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
            {
                errors.Add("Fragment steps must be named.");
            }
            else if (!ids.Add(step.Id))
            {
                errors.Add($"Duplicate execution step '{step.Id}'.");
            }
        }

        var bound = bindings.Select(node => node.StepId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!bound.Contains(id))
            {
                errors.Add($"Execution step '{id}' has no binding.");
            }
        }

        foreach (var orphan in bound.Where(id => !ids.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
        {
            errors.Add($"Binding '{orphan}' has no step.");
        }

        foreach (var step in steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!ids.Contains(dependency))
                {
                    errors.Add($"Execution step '{step.Id}' depends on unknown step '{dependency}'.");
                }
                else if (string.Equals(step.Id, dependency, StringComparison.Ordinal))
                {
                    errors.Add($"Execution step '{step.Id}' cannot depend on itself.");
                }
            }
        }

        if (HasCycle(steps, ids))
        {
            errors.Add("Assembled execution graph contains a dependency cycle.");
        }

        return errors;
    }

    private static bool HasCycle(
        IReadOnlyList<PlannedExecutionStep> steps, IReadOnlySet<string> ids)
    {
        var indegree = ids.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var step in steps)
        {
            foreach (var dependency in step.DependsOn.Distinct(StringComparer.Ordinal))
            {
                if (indegree.ContainsKey(step.Id) && indegree.ContainsKey(dependency))
                {
                    indegree[step.Id]++;
                }
            }
        }

        var queue = new Queue<string>(indegree
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key));
        var visited = 0;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            visited++;
            foreach (var step in steps)
            {
                if (step.DependsOn.Contains(id, StringComparer.Ordinal) &&
                    --indegree[step.Id] == 0)
                {
                    queue.Enqueue(step.Id);
                }
            }
        }

        return visited != ids.Count;
    }
}
