using Scriban;
using Scriban.Runtime;
using System.Collections.Concurrent;

namespace Guyabano.Llm.Prompting;

public sealed class ScribanPromptTemplateEngine(IPromptLoader promptLoader) : IPromptTemplateEngine
{
    private readonly ConcurrentDictionary<string, Template> _compiledCache = new();

    public async Task<string> RenderAsync(
        string templateName,
        object model,
        CancellationToken cancellationToken = default)
    {
        var template = await GetCompiledTemplateAsync(templateName, cancellationToken);

        var scriptObject = new ScriptObject();
        scriptObject.Import(model);

        var context = new TemplateContext { MemberRenamer = member => member.Name };
        context.PushGlobal(scriptObject);

        return await template.RenderAsync(context);
    }

    private async Task<Template> GetCompiledTemplateAsync(string templateName, CancellationToken cancellationToken)
    {
        if (_compiledCache.TryGetValue(templateName, out var cached))
            return cached;

        var raw = await promptLoader.LoadAsync(templateName, cancellationToken);
        var template = Template.Parse(raw, templateName);

        if (template.HasErrors)
        {
            throw new InvalidOperationException(
                $"Prompt template '{templateName}' failed to parse: " +
                string.Join("; ", template.Messages));
        }

        _compiledCache[templateName] = template;
        return template;
    }
}