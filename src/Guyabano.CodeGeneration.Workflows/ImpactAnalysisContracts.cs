namespace Guyabano.CodeGeneration.Workflows;

public enum CodeGenerationImpactCause
{
    Workflow,
    Artifact,
    CodeGraph
}

public sealed record CodeGenerationImpactNode(
    string StepKey,
    string? TaskId,
    CodeGenerationImpactCause Cause,
    string Reason,
    string? FilePath = null,
    string? HetuNodeId = null);

public sealed record CodeGenerationImpactReport(
    Guid WorkflowRunId,
    string? TargetStepKey,
    string? IndexIdentity,
    string? IndexRunId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CodeGenerationImpactNode> ImpactedNodes,
    IReadOnlyList<string> ReusableStepKeys)
{
    public IReadOnlyList<string> InvalidatedStepKeys =>
        ImpactedNodes.Select(node => node.StepKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

public sealed record CodeGenerationAppliedRestartPlan(
    Guid WorkflowRunId,
    string TargetStepKey,
    string ApprovedBy,
    DateTimeOffset AppliedAt,
    IReadOnlyList<string> InvalidatedStepKeys,
    IReadOnlyList<string> RerunStepKeys,
    IReadOnlyList<string> ReusableStepKeys,
    IReadOnlyList<CodeGenerationImpactNode> ImpactedNodes);
