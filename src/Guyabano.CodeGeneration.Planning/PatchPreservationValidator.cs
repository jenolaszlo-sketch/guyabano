using System.Text.Json;
using Guyabano.Artifacts;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Admission-side preservation check for patch-based authoring (spec §6.1.2).
/// The model re-emits the full Fuwen DSL from the merged design, so the
/// harness verifies the candidate plan only changes what the patch touches:
/// every node whose step id is outside <see cref="WorkflowPatch.AffectedStepIds"/>
/// must be canonically identical to the prior plan. Drift is reported as
/// repair feedback, never silently admitted.
/// </summary>
public static class PatchPreservationValidator
{
    /// <returns>Drift descriptions; empty when the candidate is clean.</returns>
    public static IReadOnlyList<string> Validate(
        WorkflowPlan prior,
        WorkflowPlan candidate,
        WorkflowPatch patch)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(patch);

        var affected = patch.AffectedStepIds();
        var drifts = new List<string>();

        var priorNodes = prior.Nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        var candidateNodes = candidate.Nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);

        foreach (var name in candidateNodes.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (affected.Contains(name))
            {
                continue;
            }

            if (!priorNodes.TryGetValue(name, out var oldNode))
            {
                drifts.Add($"Candidate adds node '{name}' outside the patch scope.");
            }
            else if (candidateNodes[name] is ReturnNode candidateReturn &&
                     oldNode is ReturnNode priorReturn)
            {
                ValidateReturn(priorReturn, candidateReturn, candidateNodes, affected, drifts);
            }
            else if (!CanonicalEquals(oldNode, candidateNodes[name]))
            {
                drifts.Add($"Candidate changes node '{name}' outside the patch scope.");
            }
        }

        foreach (var name in priorNodes.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!candidateNodes.ContainsKey(name) && !affected.Contains(name))
            {
                drifts.Add($"Candidate removes node '{name}' outside the patch scope.");
            }
        }

        ValidatePrompts(prior, candidate, affected, drifts);
        return drifts;
    }

    private static void ValidateReturn(
        ReturnNode prior,
        ReturnNode candidate,
        IReadOnlyDictionary<string, WorkflowNode> candidateNodes,
        IReadOnlySet<string> affected,
        List<string> drifts)
    {
        if (CanonicalEquals(prior, candidate))
        {
            return;
        }

        var target = candidate.Value is NodeOutputBinding output
            ? candidateNodes.Values.SingleOrDefault(node =>
                string.Equals(node.StructuralPath, output.NodePath, StringComparison.Ordinal))
            : null;
        if (target is not null && affected.Contains(target.Name))
        {
            return;
        }

        drifts.Add("Candidate changes the workflow return outside the patch scope.");
    }

    private static void ValidatePrompts(
        WorkflowPlan prior,
        WorkflowPlan candidate,
        IReadOnlySet<string> affected,
        List<string> drifts)
    {
        var priorPrompts = (prior.Prompts ?? Enumerable.Empty<PromptDefinition>())
            .ToDictionary(prompt => prompt.Name, StringComparer.Ordinal);
        var candidatePrompts = (candidate.Prompts ?? Enumerable.Empty<PromptDefinition>())
            .ToDictionary(prompt => prompt.Name, StringComparer.Ordinal);

        foreach (var name in priorPrompts.Keys
                     .Concat(candidatePrompts.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            var inPrior = priorPrompts.TryGetValue(name, out var oldPrompt);
            var inCandidate = candidatePrompts.TryGetValue(name, out var newPrompt);
            if (inPrior && inCandidate && CanonicalEquals(oldPrompt!, newPrompt!))
            {
                continue;
            }

            var referencing = prior.Nodes.OfType<InferenceNode>()
                .Concat(candidate.Nodes.OfType<InferenceNode>())
                .Where(node => string.Equals(node.PromptName, name, StringComparison.Ordinal))
                .Select(node => node.Name)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (referencing.Length == 0 || referencing.Any(step => !affected.Contains(step)))
            {
                drifts.Add(inPrior && !inCandidate
                    ? $"Candidate removes prompt '{name}' outside the patch scope."
                    : !inPrior
                        ? $"Candidate adds prompt '{name}' outside the patch scope."
                        : $"Candidate changes prompt '{name}' outside the patch scope.");
            }
        }
    }

    private static bool CanonicalEquals(object left, object right) =>
        CanonicalHash(left).Equals(CanonicalHash(right), StringComparison.Ordinal);

    private static string CanonicalHash(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, value.GetType());
        return CanonicalJsonContentHash.Compute(element);
    }
}
