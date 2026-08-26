using Penghou.Baize;

namespace Guyabano.Llm.Prompting;

public interface IPromptBuilder<in TContext>
{
    Task<LlmRequest> BuildAsync(TContext context, CancellationToken cancellationToken = default);
}