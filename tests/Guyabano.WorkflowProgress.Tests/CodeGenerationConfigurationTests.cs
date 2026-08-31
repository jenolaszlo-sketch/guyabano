using FluentAssertions;
using Guyabano.WebTerminal.Services;
using Microsoft.Extensions.Configuration;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationConfigurationTests
{
    [Fact]
    public void AddDefaults_CommandLineOverridesComponentJson()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "guyabano-configuration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "appsettings.CodeGeneration.json");
        try
        {
            File.WriteAllText(path,
                "{\"CodeGeneration\":{\"OutputRoot\":\"json-generated\"}}");
            var configuration = new ConfigurationManager();

            CodeGenerationConfiguration.AddDefaults(
                configuration,
                path,
                ["--CodeGeneration:OutputRoot=deployment-generated"]);

            configuration["CodeGeneration:OutputRoot"]
                .Should().Be("deployment-generated");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
