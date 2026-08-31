using Guyabano.Session;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationWorkflowRequest(
    string Prompt,
    GuyabanoSessionId SessionId,
    string? ResumeFromWorkflowId = null,
    CodeGenerationContinuationMode ContinuationMode =
        CodeGenerationContinuationMode.None,
    CodeGenerationWorkflowResult? ResumeFallback = null)
{
    public RepositoryReference? Repository { get; init; }

    public RepositoryContextReference? RepositoryContext { get; init; }

    /// <summary>
    /// Number of model tiers captured when the durable run starts. Persisting
    /// this keeps Zhinu's retry policy aligned with the worker configuration
    /// even when the workflow is replayed later.
    /// </summary>
    public int GenerationModelTierCount { get; init; } =
        CodeGenerationWorkflowConstants.MaximumModelTiers;
}
