using Guyabano.Llm.Prompting;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Minimal wrapper around <see cref="CodeGenerationTaskContext"/> with typed
/// cross-system references. Keeps Pass 1b minimal while satisfying the
/// requirement for Cangjie/Hetu correlation without storing rendered prompts.
/// </summary>
public sealed record TaskContextArtifactPayload(
    CodeGenerationTaskContext Context,
    string SessionId,
    string WorkflowRunId,
    string StepKey,
    int StepRevision,
    Guid? CangjieSnapshotId,
    string? CangjieStrategy,
    string? CangjieStrategyVersion,
    string? HetuIndexRunId,
    string? HetuIndexIdentity,
    string? HetuProviderSnapshotIdentity,
    CodeGenerationTaskRetryContext? RetryContext);
