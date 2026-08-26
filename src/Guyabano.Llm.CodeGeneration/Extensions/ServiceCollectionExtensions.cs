using Microsoft.Extensions.DependencyInjection;

using Guyabano.CodeGeneration.Validation.Extensions;

namespace Guyabano.Llm.CodeGeneration.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmCodeGeneration(this IServiceCollection services)
    {
        services.AddCodeGenerationValidation();

        services.AddSingleton<ICodeGenerationResultParser, CodeGenerationResultParser>();
        services.AddSingleton<ICodeEmitter, FileSystemCodeEmitter>();
        services.AddSingleton<ICodeGenerationService, CodeGenerationService>();
        services.AddSingleton<ICodeGenerationTaskService>(serviceProvider =>
            (CodeGenerationService)serviceProvider.GetRequiredService<
                ICodeGenerationService>());

        return services;
    }
}
