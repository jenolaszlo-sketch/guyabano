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
            BuildResponseFormat(context));
    }
}
