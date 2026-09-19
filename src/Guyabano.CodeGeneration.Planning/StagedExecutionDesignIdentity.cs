using System.Text.Json;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Stable identity for a resolved execution design. Steps and bindings are
/// order-normalized before hashing, so two designs built from the same
/// artifacts in a different traversal order share one fingerprint. The
/// fingerprint pins the base a <see cref="WorkflowPatch"/> applies to; a
/// patch authored against any other design is stale and rejected.
/// </summary>
public static class StagedExecutionDesignIdentity
{
    public const string FingerprintScheme = "sha256:execution-design/v1:";

    public static string Compute(PlannedExecutionDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["workflowName"] = design.Graph.WorkflowName,
            ["inputType"] = design.Graph.InputType,
            ["outputType"] = design.Graph.OutputType,
            ["steps"] = design.Graph.Steps
                .OrderBy(step => step.Id, StringComparer.Ordinal)
                .Select(step => JsonSerializer.SerializeToElement(step))
                .ToArray(),
            ["bindings"] = design.Bindings.Nodes
                .OrderBy(node => node.StepId, StringComparer.Ordinal)
                .Select(node => JsonSerializer.SerializeToElement(node))
                .ToArray(),
        };
        var element = JsonSerializer.SerializeToElement(normalized);
        return FingerprintScheme + CanonicalJsonContentHash.Compute(element);
    }
}
