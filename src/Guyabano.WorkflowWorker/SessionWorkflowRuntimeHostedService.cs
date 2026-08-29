using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Penghou.Zhinu;
using Guyabano.Session;

namespace Guyabano.WorkflowWorker;

/// <summary>Scans each cataloged session's independent Zhinu store.</summary>
public sealed class SessionWorkflowRuntimeHostedService(
    IGuyabanoSessionStore sessionStore,
    ISessionWorkflowRuntimeProvider runtimeProvider,
    ZhinuOptions options,
    TimeProvider timeProvider,
    ILogger<SessionWorkflowRuntimeHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Per-session Zhinu workflow execution started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                var sessions = await sessionStore.ListAsync(stoppingToken).ConfigureAwait(false);
                await Parallel.ForEachAsync(
                    sessions,
                    new ParallelOptions
                    {
                        CancellationToken = stoppingToken,
                        MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrentWorkflows)
                    },
                    async (session, cancellationToken) =>
                    {
                        await using var runtime = await runtimeProvider
                            .AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false);
                        var sessionProcessed = await runtime.Engine
                            .RunAvailableAsync(cancellationToken).ConfigureAwait(false);
                        Interlocked.Add(ref processed, sessionProcessed);
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Per-session Zhinu execution scan failed; the next scan will retry.");
            }

            if (processed == 0)
                await Task.Delay(options.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
        logger.LogInformation("Per-session Zhinu workflow execution stopped.");
    }
}
