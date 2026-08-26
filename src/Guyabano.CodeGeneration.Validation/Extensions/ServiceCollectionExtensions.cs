using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Guyabano.CodeGeneration.Validation.Validators;

namespace Guyabano.CodeGeneration.Validation.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeGenerationValidation(
        this IServiceCollection services,
        Action<CodeGenerationValidationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<
            IGeneratedFileValidationPipeline,
            GeneratedFileValidationPipeline>();

        var builder = new CodeGenerationValidationBuilder(services);

        builder
            .AddValidator<CSharpSyntaxValidator>(".cs")
            .AddValidator<JsonSyntaxValidator>(".json")
            .AddValidator<XmlSyntaxValidator>(
                ".xml",
                ".csproj",
                ".props",
                ".targets");

        configure?.Invoke(builder);

        return services;
    }
}
