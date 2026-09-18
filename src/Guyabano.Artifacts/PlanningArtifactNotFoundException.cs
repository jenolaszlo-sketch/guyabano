namespace Guyabano.Artifacts;

/// <summary>
/// Raised when a planning artifact revision referenced as an input or
/// invalidation root does not exist in the workflow's catalog.
/// </summary>
public sealed class PlanningArtifactNotFoundException : Exception
{
    public PlanningArtifactNotFoundException(string workflowId, PlanningArtifactVersion version)
        : base($"Planning artifact '{version.Value}' does not exist in workflow '{workflowId}'.")
    {
        WorkflowId = workflowId;
        Version = version;
    }

    public string WorkflowId { get; }

    public PlanningArtifactVersion Version { get; }
}
