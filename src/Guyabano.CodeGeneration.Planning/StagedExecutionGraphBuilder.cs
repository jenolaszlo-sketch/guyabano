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
