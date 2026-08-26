using Penghou.Baize;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

public sealed class DomainDiscoveryPromptBuilder(
    IPromptTemplateEngine templateEngine)
    : PromptBuilderBase<DomainDiscoveryPromptContext>(templateEngine),
      IPromptBuilder<DomainDiscoveryPromptContext>
{
    protected override PromptTemplate Template { get; } = new(
        "domain-discovery/system.sbn",
        "domain-discovery/user.sbn");

    protected override void Validate(DomainDiscoveryPromptContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Request);
        ArgumentNullException.ThrowIfNull(context.ResponseFormat);
    }

    protected override object BuildTemplateModel(
        DomainDiscoveryPromptContext context) => new
        {
            Request = context.Request.Trim(),
            context.PreviousFailure
        };

    protected override LlmResponseFormat? BuildResponseFormat(
        DomainDiscoveryPromptContext context) => context.ResponseFormat;
}
