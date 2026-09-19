using System.Text;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Renders a resolved execution design as the execution-plan section the
/// workflow-authoring pack injects. The text is the authoritative
/// decomposition the model translates; step ids, dependency edges, pinned
/// artifact versions, and descriptor references are rendered exactly so the
/// compiler can verify fidelity.
/// </summary>
public static class PlannedExecutionDesignSummary
{
    public static string Render(PlannedExecutionDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        var bindings = design.Bindings.Nodes
            .ToDictionary(node => node.StepId, node => node.Binding, StringComparer.Ordinal);
        var builder = new StringBuilder();
        builder.Append("Workflow: ");
        builder.Append(design.Graph.WorkflowName);
        builder.Append("(input: ");
        builder.Append(design.Graph.InputType);
        builder.Append(") -> ");
        builder.AppendLine(design.Graph.OutputType);
        builder.AppendLine();
        builder.AppendLine("Steps:");
        foreach (var step in design.Graph.Steps)
        {
            if (!bindings.TryGetValue(step.Id, out var binding))
            {
                throw new InvalidOperationException(
                    $"Execution step '{step.Id}' has no binding.");
            }

            builder.Append("- ");
            builder.Append(step.Id);
            builder.Append(" [");
            builder.Append(binding.Descriptor.Kind);
            builder.Append(" \"");
            builder.Append(Reference(binding.Descriptor));
            builder.Append("\" capability=");
            builder.Append(binding.Capability);
            builder.Append(" profile=");
            builder.Append(binding.ModelProfile);
            builder.Append(" role=");
            builder.AppendLine(binding.Role + "]");
            builder.Append("  title: ");
            builder.AppendLine(step.Title);
            builder.Append("  depends on: ");
            builder.AppendLine(step.DependsOn.Count == 0
                ? "(none)"
                : string.Join(", ", step.DependsOn));
            builder.Append("  requires artifacts: ");
            builder.AppendLine(step.RequiredArtifacts.Count == 0
                ? "(none)"
                : string.Join(", ", step.RequiredArtifacts));
        }

        return builder.ToString();
    }

    /// <summary>Exact quoted descriptor reference for Fuwen source.</summary>
    public static string Reference(DescriptorReference descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return $"{descriptor.Name}@{descriptor.Version}#{descriptor.ContentDigest.Value}";
    }
}
