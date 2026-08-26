using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Penghou.Baize;
using Penghou.Baize.Claude;
using Penghou.Baize.Gemini;
using Penghou.Baize.Ollama;
using Penghou.Baize.OpenAi;
using Penghou.Baize.Router;
using Penghou.Baize.Router.Extensions;

namespace Guyabano.WorkflowProgressTests;

public sealed class BaizeRoutingConfigurationTests
{
    [Fact]
    public void WorkerConfiguration_RegistersEveryConfiguredBaizeModel()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "worker-appsettings.json"))
            .Build();
        var configuredModels = configuration
            .GetSection("LlmRouting:Models")
            .GetChildren()
            .Select(model => model["Name"]!)
            .ToArray();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddOpenAiLlmProvider();
        services.AddClaudeLlmProvider();
        services.AddGeminiLlmProvider();
        services.AddOllamaLlmProvider();

        services.AddLlmRouting(configuration);

        using var provider = services.BuildServiceProvider();
        var lookup = provider.GetRequiredService<ILlmModelLookup>();
        configuredModels.Should().HaveCount(23);
        foreach (var model in configuredModels)
            lookup.GetClient(model).Should().NotBeNull();
    }

    [Theory]
    [InlineData("deepseek-v4-flash")]
    [InlineData("deepseek-v4-pro")]
    public async Task WorkerConfiguration_DeepSeekModelsAcceptStructuredOutput(
        string model)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "worker-appsettings.json"))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddOpenAiLlmProvider();
        services.AddClaudeLlmProvider();
        services.AddGeminiLlmProvider();
        services.AddOllamaLlmProvider();
        services.AddLlmRouting(configuration);

        await using var provider = services.BuildServiceProvider();
        var router = provider.GetRequiredService<ILlmRouter>();
        var request = new LlmRequest(
            [new LlmMessage("user", "Return JSON")],
            responseFormat: LlmResponseFormat.JsonSchema(
                """{"type":"object"}"""));

        var explanation = await router.ExplainModelAsync(
            model,
            request,
            TestContext.Current.CancellationToken);

        explanation.Succeeded.Should().BeTrue();
        explanation.SelectedEndpoint!.Value.Model.Should().Be(model);
        explanation.SelectedEndpoint.Value.Provider.Should().Be(
            new LlmProviderKey("OpenAi"));
    }

    [Theory]
    [InlineData("gemini-3.6-flash")]
    [InlineData("gemini-3.5-flash")]
    [InlineData("gemini-3.5-flash-lite")]
    public async Task WorkerConfiguration_GeminiAcceptsStructuredOutput(
        string model)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "worker-appsettings.json"))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddOpenAiLlmProvider();
        services.AddClaudeLlmProvider();
        services.AddGeminiLlmProvider();
        services.AddOllamaLlmProvider();
        services.AddLlmRouting(configuration);

        await using var provider = services.BuildServiceProvider();
        var router = provider.GetRequiredService<ILlmRouter>();
        var request = new LlmRequest(
            [new LlmMessage("user", "Return JSON")],
            responseFormat: LlmResponseFormat.JsonSchema(
                """{"type":"object"}"""));

        var explanation = await router.ExplainModelAsync(
            model,
            request,
            TestContext.Current.CancellationToken);

        explanation.Succeeded.Should().BeTrue();
        explanation.SelectedEndpoint!.Value.Provider.Should().Be(
            new LlmProviderKey("Gemini"));
    }
}
