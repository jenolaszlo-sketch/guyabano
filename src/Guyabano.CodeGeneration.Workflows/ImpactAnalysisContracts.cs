using Guyabano.Artifacts;
using Penghou.Zhinu;

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
    Guid PreviewId,
    Guid WorkflowRunId,
    string? TargetStepKey,
    StepRestartMode RestartMode,
    string? WorkspaceRevision,
    string ChangeSetHash,
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

public sealed record CodeGenerationImpactProposal(
    ArtifactReference Artifact,
    CodeGenerationImpactReport Impact);

public sealed record CodeGenerationRestartApprovalCommand(
    Guid ApprovalId,
    CodeGenerationImpactProposal Proposal,
    DateTimeOffset ApprovedAt);

public sealed record CodeGenerationAppliedRestartPlan(
    Guid ApprovalId,
    Guid PreviewId,
    Guid WorkflowRunId,
    string TargetStepKey,
    string? WorkspaceRevision,
    string? IndexIdentity,
    string ChangeSetHash,
    string ApprovedBy,
    DateTimeOffset AppliedAt,
    IReadOnlyList<string> InvalidatedStepKeys,
    IReadOnlyList<string> RerunStepKeys,
    IReadOnlyList<string> ReusableStepKeys,
    IReadOnlyList<CodeGenerationImpactNode> ImpactedNodes);
