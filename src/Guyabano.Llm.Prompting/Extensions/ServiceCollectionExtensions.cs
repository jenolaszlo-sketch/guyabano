using Microsoft.Extensions.DependencyInjection;
using Penghou.Guihua.Baize;

namespace Guyabano.Llm.Prompting.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmPrompting(this IServiceCollection services, string promptRoot)
    {
        services.AddSingleton<IPromptLoader>(new FilePromptLoader(promptRoot));
        services.AddSingleton<IPromptTemplateEngine, ScribanPromptTemplateEngine>();
        services.AddSingleton<IPromptBuilder<CodeGenerationPromptContext>, CodeGenerationPromptBuilder>();
        services.AddSingleton<IPromptBuilder<CodeGenerationTaskPromptContext>, CodeGenerationTaskPromptBuilder>();
        //services.AddSingleton<IPromptBuilder<CodeGenFeedbackPromptContext>, CodeGenFeedbackPromptBuilder>();

        return services;
    }
}
