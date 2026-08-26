using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Guyabano.CodeGeneration.Validation;

public sealed class CodeGenerationValidationBuilder
{
    private readonly IServiceCollection services;

    internal CodeGenerationValidationBuilder(IServiceCollection services)
    {
        this.services = services;
    }

    public CodeGenerationValidationBuilder AddValidator<TValidator>(
        params string[] extensions)
        where TValidator : class, IGeneratedFileValidator
    {
        ArgumentNullException.ThrowIfNull(extensions);

        if (extensions.Length == 0)
        {
            throw new ArgumentException(
                "At least one file extension is required.",
                nameof(extensions));
        }

        services.TryAddSingleton<TValidator>();

        foreach (var extension in extensions)
        {
            var normalizedExtension = NormalizeExtension(extension);

            services.AddSingleton(serviceProvider =>
                new GeneratedFileValidatorRegistration(
                    normalizedExtension,
                    serviceProvider.GetRequiredService<TValidator>()));
        }

        return this;
    }

    private static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var trimmed = extension.Trim();

        return trimmed.StartsWith('.')
            ? trimmed.ToLowerInvariant()
            : $".{trimmed.ToLowerInvariant()}";
    }
}
