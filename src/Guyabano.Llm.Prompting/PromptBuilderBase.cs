using Scriban.Parsing;
using Penghou.Baize;

namespace Guyabano.Llm.Prompting;

public abstract class PromptBuilderBase<TContext>(
    IPromptTemplateEngine templateEngine)
    where TContext : ILlmPromptContext
{
    protected abstract PromptTemplate Template { get; }

    /// <summary>Validate context; throw ArgumentException/ArgumentNullException on failure.</summary>
    protected abstract void Validate(TContext context);

    /// <summary>Shape the context into whatever the Scriban template expects as variables.</summary>
    protected abstract object BuildTemplateModel(TContext context);

    /// <summary>Override to attach tools; default is none.</summary>
    protected virtual IReadOnlyList<LlmTool> BuildTools(TContext context) => [];

    /// <summary>Override to request structured output; default is unconstrained text.</summary>
    protected virtual LlmResponseFormat? BuildResponseFormat(TContext context) => null;

    /// <summary>Override to attach host-neutral routing and telemetry metadata.</summary>
    protected virtual IReadOnlyDictionary<string, object?> BuildMetadata(
        TContext context) => new Dictionary<string, object?>(
            StringComparer.Ordinal);

    public async Task<LlmRequest> BuildAsync(TContext context, CancellationToken cancellationToken = default)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        Validate(context);

        var model = BuildTemplateModel(context);
        var systemPrompt = await templateEngine.RenderAsync(
            Template.SystemPromptName,
            model,
            cancellationToken);
        var userPrompt = await templateEngine.RenderAsync(Template.UserTemplateName, model, cancellationToken);

        return new LlmRequest(
            [
                new LlmMessage("system", systemPrompt),
                new LlmMessage("user", userPrompt)
            ],
            context.Temperature,
            context.MaxTokens,
            BuildTools(context).ToList(),
            BuildResponseFormat(context),
            metadata: MergeMetadata(context));
    }

    private IReadOnlyDictionary<string, object?> MergeMetadata(TContext context)
    {
        var metadata = new Dictionary<string, object?>(
            BuildMetadata(context),
            StringComparer.Ordinal);
        var correlation = LlmRequestCorrelationScope.Current;
        if (correlation is null)
            return metadata;

        metadata.TryAdd("guyabano.session_id", correlation.SessionId);
        metadata.TryAdd("guyabano.workflow_run_id", correlation.WorkflowRunId);
        metadata.TryAdd("guyabano.workflow_step_key", correlation.WorkflowStepKey);
        return metadata;
    }
}
