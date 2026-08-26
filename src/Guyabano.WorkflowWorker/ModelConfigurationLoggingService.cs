using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Guyabano.WorkflowWorker;

internal sealed class ModelConfigurationLoggingService(
    IOptions<CodeGenerationWorkerOptions> options,
    ILogger<ModelConfigurationLoggingService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (configured.EscalationModels.Count == 0)
        {
            logger.LogWarning(
                "No code-generation fallback model is configured. Generation will retry only {Model}.",
                configured.Model);
        }

        if (configured.DecompositionEscalationModels.Count == 0)
        {
            logger.LogWarning(
                "No decomposition fallback model is configured. Decomposition will retry only {Model}.",
                configured.DecompositionModel);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
