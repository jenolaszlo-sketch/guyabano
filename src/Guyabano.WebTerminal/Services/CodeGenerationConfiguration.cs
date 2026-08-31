using Microsoft.Extensions.Configuration;

namespace Guyabano.WebTerminal.Services;

internal static class CodeGenerationConfiguration
{
    public static void AddDefaults(
        ConfigurationManager configuration,
        string defaultsPath,
        string[] args)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultsPath);
        ArgumentNullException.ThrowIfNull(args);

        configuration.AddJsonFile(
            defaultsPath,
            optional: false,
            reloadOnChange: true);

        // CreateBuilder registered these providers before this component file.
        // Append them again so deployment configuration remains authoritative.
        configuration.AddEnvironmentVariables();
        if (args.Length > 0)
            configuration.AddCommandLine(args);
    }
}
