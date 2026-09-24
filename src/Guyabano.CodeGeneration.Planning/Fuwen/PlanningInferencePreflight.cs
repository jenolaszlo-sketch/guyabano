using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Provider-neutral preflight shared by Guyabano's one-call planning-stage
/// inference executors. They run exactly one provider call, never
/// model-callable tools, and produce text output only; requirements outside
/// that shape are rejected at admission instead of failing at runtime.
/// </summary>
internal static class PlanningInferencePreflight
{
    public static ExecutionFailure? Check(InferenceExecutionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (requirement.Tools.Count > 0)
        {
            return new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.NotAdmitted,
                "The planning stage executor runs a single provider call without model-callable tools.");
        }

        if (requirement.Modality is not null)
        {
            return new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.NotAdmitted,
                "The planning stage executor supports text generation only.");
        }

        return null;
    }
}
