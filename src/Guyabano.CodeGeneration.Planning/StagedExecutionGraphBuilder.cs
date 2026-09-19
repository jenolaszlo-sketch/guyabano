using System.Text;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Deterministically derives the semantic execution graph and per-step
/// bindings from staged planning artifacts and their pinned catalog versions.
/// The graph expresses what to do and in which order; bindings record how each
/// step is performed (capability, model profile, resolved descriptor). No
/// model call is involved: this is compilation from planning artifacts, not
/// rediscovery.
/// </summary>
public static class StagedExecutionGraphBuilder
{
    public const string IntegrateStepId = "integrate";
    public const string TestStepId = "test";

    private static readonly IReadOnlyDictionary<string, (string Capability, string ModelProfile)> RoleContracts =
        new Dictionary<string, (string Capability, string ModelProfile)>(StringComparer.Ordinal)
        {
            [PlannedExecutionRoles.Implement] = ("code.modify", "implementation"),
            [PlannedExecutionRoles.Integrate] = ("code.modify", "integration"),
            [PlannedExecutionRoles.Verify] = ("process.execute", "verification"),
        };

    /// <summary>
    /// Builds the execution design. <paramref name="descriptorsByRole"/>
    /// resolves the implement/integrate/verify roles to exact trusted
    /// descriptors; every role must be present.
    /// </summary>
    public static PlannedExecutionDesign Build(
        StagedPlanningArtifacts artifacts,
        StagedPlanningArtifactVersions versions,
        IReadOnlyDictionary<string, TrustedCatalogueDescriptor> descriptorsByRole,
        string workflowName = "implementation")
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(descriptorsByRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);
        foreach (var role in RoleContracts.Keys)
        {
            if (!descriptorsByRole.TryGetValue(role, out var descriptor) ||
                descriptor is null)
            {
                throw new InvalidOperationException(
                    $"Execution design requires a trusted descriptor for role '{role}'.");
            }
        }

        var ordered = TopologicalContexts.Order(artifacts.Topology.BoundedContexts);
        var implementIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = new List<PlannedExecutionStep>();
        var bindings = new List<PlannedNodeBinding>();
        var allContracts = new List<string>();
        var allComponents = new List<string>();

        foreach (var context in ordered)
        {
            if (!versions.Contracts.TryGetValue(context.Name, out var contractVersion))
            {
                throw new InvalidOperationException(
                    $"Bounded context '{context.Name}' has no published contract artifact.");
            }

            var id = StepId(PlannedExecutionRoles.Implement, context.Name);
            var dependencies = context.DependsOnContextNames
                .Select(name => implementIds.TryGetValue(name, out var dependency)
                    ? dependency
                    : throw new InvalidOperationException(
                        $"Bounded context '{context.Name}' depends on unknown context '{name}'."))
                .ToArray();
            steps.Add(new PlannedExecutionStep
            {
                Id = id,
                Title = $"Implement {context.Name}",
                DependsOn = dependencies,
                RequiredArtifacts = [contractVersion.Value],
                AcceptanceCriteria = [],
            });
            bindings.Add(Bind(
                id,
                PlannedExecutionRoles.Implement,
                descriptorsByRole[PlannedExecutionRoles.Implement].Descriptor,
                [contractVersion.Value, versions.Topology.Value]));
            implementIds[context.Name] = id;
            allContracts.Add(contractVersion.Value);
        }

        foreach (var manifest in versions.Components)
        {
            allComponents.Add(manifest.Value.Value);
        }

        var implementStepIds = ordered
            .Select(context => implementIds[context.Name])
            .ToArray();
        steps.Add(new PlannedExecutionStep
        {
            Id = IntegrateStepId,
            Title = "Integrate the implemented contexts",
            DependsOn = implementStepIds,
            RequiredArtifacts = allContracts,
            AcceptanceCriteria = [],
        });
        bindings.Add(Bind(
            IntegrateStepId,
            PlannedExecutionRoles.Integrate,
            descriptorsByRole[PlannedExecutionRoles.Integrate].Descriptor,
            [.. allContracts, versions.Topology.Value]));

        steps.Add(new PlannedExecutionStep
        {
            Id = TestStepId,
            Title = "Verify the integrated implementation",
            DependsOn = [IntegrateStepId],
            RequiredArtifacts = allComponents,
            AcceptanceCriteria = [],
        });
        bindings.Add(Bind(
            TestStepId,
            PlannedExecutionRoles.Verify,
            descriptorsByRole[PlannedExecutionRoles.Verify].Descriptor,
            [.. allComponents, versions.Topology.Value]));

        return new PlannedExecutionDesign(
            new PlannedExecutionGraph
            {
                WorkflowName = workflowName,
                InputType = "string",
                OutputType = "string",
                Steps = steps,
            },
            new PlannedExecutionBindings
            {
                WorkflowName = workflowName,
                Nodes = bindings,
            });
    }

    private static PlannedNodeBinding Bind(
        string stepId,
        string role,
        DescriptorReference descriptor,
        IReadOnlyList<string> contextArtifacts)
    {
        var (capability, profile) = RoleContracts[role];
        return new PlannedNodeBinding
        {
            StepId = stepId,
            Binding = new PlannedExecutionBinding
            {
                Role = role,
                Capability = capability,
                ModelProfile = profile,
                ContextArtifacts = contextArtifacts,
                Descriptor = descriptor,
            },
        };
    }

    /// <summary>
    /// Applies a <see cref="WorkflowPatch"/> to a resolved execution design.
    /// The patch must be authored against <paramref name="current"/> (pinned
    /// by base fingerprint); steps and bindings the patch does not mention
    /// are carried forward unchanged so their Fuwen fingerprints stay stable.
    /// An empty patch is a valid no-op: the result fingerprints identically
    /// to the current design. Throws <see cref="InvalidOperationException"/>
    /// for stale bases, unknown references, orphaned dependencies, cycles,
    /// or binding gaps.
    /// </summary>
    public static PlannedExecutionDesign Apply(
        PlannedExecutionDesign current,
        WorkflowPatch patch)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentException.ThrowIfNullOrWhiteSpace(patch.Rationale);
        if (patch.DerivedFromArtifacts.Count == 0)
        {
            throw new InvalidOperationException(
                "Workflow patch must name the artifact revisions it derives from.");
        }

        var expectedBase = StagedExecutionDesignIdentity.Compute(current);
        if (!string.Equals(
                patch.BaseDesignFingerprint,
                expectedBase,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workflow patch was authored against a different execution design and is stale.");
        }

        ValidatePatchLists(patch);

        var currentSteps = current.Graph.Steps
            .ToDictionary(step => step.Id, StringComparer.Ordinal);
        var currentBindings = current.Bindings.Nodes
            .ToDictionary(node => node.StepId, StringComparer.Ordinal);

        foreach (var id in patch.ReplaceSteps.Select(step => step.Id)
                     .Concat(patch.RemoveStepIds)
                     .Concat(patch.DependencyEdits.Select(edit => edit.StepId)))
        {
            if (!currentSteps.ContainsKey(id))
            {
                throw new InvalidOperationException(
                    $"Workflow patch references unknown execution step '{id}'.");
            }
        }

        foreach (var step in patch.AddSteps)
        {
            if (currentSteps.ContainsKey(step.Id))
            {
                throw new InvalidOperationException(
                    $"Workflow patch adds execution step '{step.Id}' which already exists.");
            }
        }

        foreach (var binding in patch.AddBindings)
        {
            if (!patch.AddSteps.Any(step =>
                    string.Equals(step.Id, binding.StepId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Workflow patch adds a binding for '{binding.StepId}' without adding that step.");
            }
        }

        foreach (var binding in patch.ReplaceBindings)
        {
            if (!currentBindings.ContainsKey(binding.StepId) ||
                patch.RemoveStepIds.Contains(binding.StepId, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Workflow patch replaces the binding of unknown or removed step '{binding.StepId}'.");
            }
        }

        var removed = patch.RemoveStepIds.ToHashSet(StringComparer.Ordinal);
        var mergedSteps = new Dictionary<string, PlannedExecutionStep>(StringComparer.Ordinal);
        foreach (var step in current.Graph.Steps)
        {
            if (!removed.Contains(step.Id))
            {
                mergedSteps[step.Id] = step;
            }
        }

        foreach (var step in patch.ReplaceSteps)
        {
            mergedSteps[step.Id] = step;
        }

        foreach (var step in patch.AddSteps)
        {
            mergedSteps.Add(step.Id, step);
        }

        foreach (var edit in patch.DependencyEdits)
        {
            mergedSteps[edit.StepId] = mergedSteps[edit.StepId] with
            {
                DependsOn = edit.DependsOn.ToArray(),
            };
        }

        foreach (var step in mergedSteps.Values)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!mergedSteps.ContainsKey(dependency))
                {
                    throw new InvalidOperationException(
                        $"Execution step '{step.Id}' depends on unknown step '{dependency}'.");
                }

                if (string.Equals(step.Id, dependency, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Execution step '{step.Id}' cannot depend on itself.");
                }
            }
        }

        var mergedBindings = new Dictionary<string, PlannedNodeBinding>(StringComparer.Ordinal);
        foreach (var node in current.Bindings.Nodes)
        {
            if (!removed.Contains(node.StepId))
            {
                mergedBindings[node.StepId] = node;
            }
        }

        foreach (var binding in patch.ReplaceBindings)
        {
            mergedBindings[binding.StepId] = binding;
        }

        foreach (var binding in patch.AddBindings)
        {
            mergedBindings.Add(binding.StepId, binding);
        }

        foreach (var id in mergedSteps.Keys)
        {
            if (!mergedBindings.ContainsKey(id))
            {
                throw new InvalidOperationException(
                    $"Execution step '{id}' has no binding.");
            }
        }

        var orphaned = mergedBindings.Keys
            .Where(id => !mergedSteps.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (orphaned.Length > 0)
        {
            throw new InvalidOperationException(
                $"Workflow patch leaves bindings without steps: {string.Join(", ", orphaned)}.");
        }

        RequireAcyclic(mergedSteps);

        var orderedSteps = current.Graph.Steps
            .Where(step => mergedSteps.ContainsKey(step.Id))
            .Select(step => mergedSteps[step.Id])
            .Concat(patch.AddSteps.Select(step => mergedSteps[step.Id]))
            .ToList();
        var orderedBindings = current.Bindings.Nodes
            .Where(node => mergedBindings.ContainsKey(node.StepId))
            .Select(node => mergedBindings[node.StepId])
            .Concat(patch.AddBindings.Select(binding => mergedBindings[binding.StepId]))
            .ToList();

        return new PlannedExecutionDesign(
            new PlannedExecutionGraph
            {
                WorkflowName = current.Graph.WorkflowName,
                InputType = current.Graph.InputType,
                OutputType = current.Graph.OutputType,
                Steps = orderedSteps,
            },
            new PlannedExecutionBindings
            {
                WorkflowName = current.Bindings.WorkflowName,
                Nodes = orderedBindings,
            });
    }

    private static void ValidatePatchLists(WorkflowPatch patch)
    {
        RequireDistinct(
            patch.AddSteps.Select(step => step.Id),
            "added steps");
        RequireDistinct(
            patch.ReplaceSteps.Select(step => step.Id),
            "replaced steps");
        RequireDistinct(patch.RemoveStepIds, "removed steps");
        RequireDistinct(
            patch.AddBindings.Select(binding => binding.StepId),
            "added bindings");
        RequireDistinct(
            patch.ReplaceBindings.Select(binding => binding.StepId),
            "replaced bindings");
        RequireDistinct(
            patch.DependencyEdits.Select(edit => edit.StepId),
            "dependency edits");

        var added = patch.AddSteps.Select(step => step.Id)
            .ToHashSet(StringComparer.Ordinal);
        var replaced = patch.ReplaceSteps.Select(step => step.Id)
            .ToHashSet(StringComparer.Ordinal);
        var overlap = added
            .Intersect(replaced, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (overlap.Length > 0)
        {
            throw new InvalidOperationException(
                $"Workflow patch both adds and replaces steps: {string.Join(", ", overlap)}.");
        }

        var removed = patch.RemoveStepIds.ToHashSet(StringComparer.Ordinal);
        var removedOverlap = replaced
            .Intersect(removed, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (removedOverlap.Length > 0)
        {
            throw new InvalidOperationException(
                $"Workflow patch both replaces and removes steps: {string.Join(", ", removedOverlap)}.");
        }

        var editedRemoved = patch.DependencyEdits.Select(edit => edit.StepId)
            .Intersect(removed, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (editedRemoved.Length > 0)
        {
            throw new InvalidOperationException(
                $"Workflow patch edits dependencies of removed steps: {string.Join(", ", editedRemoved)}.");
        }
    }

    private static void RequireDistinct(IEnumerable<string> ids, string listName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = ids.Where(id => !seen.Add(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Workflow patch contains duplicate {listName}: {string.Join(", ", duplicates)}.");
        }
    }

    private static void RequireAcyclic(IReadOnlyDictionary<string, PlannedExecutionStep> steps)
    {
        var indegree = steps.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var step in steps.Values)
        {
            foreach (var dependency in step.DependsOn.Distinct(StringComparer.Ordinal))
            {
                indegree[step.Id]++;
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
            foreach (var step in steps.Values)
            {
                if (step.DependsOn.Contains(id, StringComparer.Ordinal) &&
                    --indegree[step.Id] == 0)
                {
                    queue.Enqueue(step.Id);
                }
            }
        }

        if (visited != steps.Count)
        {
            throw new InvalidOperationException(
                "Workflow patch produces a dependency cycle.");
        }
    }

    /// <summary>
    /// Fuwen-safe step id: lowercase letters, digits, and underscores only.
    /// </summary>
    public static string StepId(string prefix, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var builder = new StringBuilder(prefix.Length + name.Length + 1);
        builder.Append(prefix);
        builder.Append('_');
        foreach (var character in name.Normalize())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var id = builder.ToString().TrimEnd('_');
        if (id.Length <= prefix.Length + 1)
            throw new InvalidOperationException($"Unable to derive a step id from '{name}'.");
        return id;
    }
}
