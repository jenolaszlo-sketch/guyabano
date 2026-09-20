using Penghou.Guihua.Baize;

namespace Guyabano.Llm.Prompting;

/// <summary>
/// Prompt-builder base that merges Guyabano's ambient request correlation
/// (session, workflow, Cangjie, Hetu, workspace) into LLM request metadata.
/// The neutral <see cref="PromptBuilderBase{TContext}"/> carries no
/// product correlation; Guyabano builders extend this instead.
/// </summary>
public abstract class CorrelatedPromptBuilderBase<TContext>(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<TContext>(templateEngine)
    where TContext : ILlmPromptContext
{
    protected override IReadOnlyDictionary<string, object?> BuildMetadata(TContext context)
    {
        var metadata = new Dictionary<string, object?>(
            base.BuildMetadata(context), StringComparer.Ordinal);
        var correlation = LlmRequestCorrelationScope.Current;
        if (correlation is null)
            return metadata;

        metadata.TryAdd("guyabano.session_id", correlation.SessionId);
        metadata.TryAdd("guyabano.workflow_run_id", correlation.WorkflowRunId);
        metadata.TryAdd("guyabano.workflow_step_key", correlation.WorkflowStepKey);
        if (correlation.CangjieSnapshotId is not null)
            metadata.TryAdd("guyabano.cangjie_snapshot_id", correlation.CangjieSnapshotId.Value.ToString("D"));
        if (correlation.CangjieStrategy is not null)
            metadata.TryAdd("guyabano.cangjie_strategy", correlation.CangjieStrategy);
        if (correlation.CangjieStrategyVersion is not null)
            metadata.TryAdd("guyabano.cangjie_strategy_version", correlation.CangjieStrategyVersion);
        if (correlation.CangjieQueryIdentity is not null)
            metadata.TryAdd("guyabano.cangjie_query_identity", correlation.CangjieQueryIdentity);
        if (correlation.CangjiePurpose is not null)
            metadata.TryAdd("guyabano.cangjie_purpose", correlation.CangjiePurpose);
        if (correlation.HetuIndexRunId is not null)
            metadata.TryAdd("guyabano.hetu_index_run_id", correlation.HetuIndexRunId);
        if (correlation.HetuIndexIdentity is not null)
            metadata.TryAdd("guyabano.hetu_index_identity", correlation.HetuIndexIdentity);
        if (correlation.WorkspaceRevision is not null)
            metadata.TryAdd("guyabano.workspace_revision", correlation.WorkspaceRevision);
        if (correlation.WorkflowStepRevision is not null)
            metadata.TryAdd("guyabano.workflow_step_revision", correlation.WorkflowStepRevision.Value);
        return metadata;
    }
}
