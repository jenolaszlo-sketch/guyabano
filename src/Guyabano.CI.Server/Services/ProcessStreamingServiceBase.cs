using Guyabano.CI.Contracts;
using System.Runtime.CompilerServices;

namespace Guyabano.CI.Server.Services;

public abstract class ProcessStreamingServiceBase<TRequest>(
    SafePathResolver safePathResolver,
    ProcessRunner processRunner)
    where TRequest : CiOperationRequest
{
    public async IAsyncEnumerable<CiStreamEvent> RunAsync(
        TRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? error = null;
        string? workingDirectory = null;

        try
        {
            ValidateRequest(request);
            workingDirectory = safePathResolver.Resolve(
                request.RelativePath);

            if (!Directory.Exists(workingDirectory))
            {
                error =
                    $"Working directory does not exist: {workingDirectory}";
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            error = exception.Message;
        }

        if (error is not null || workingDirectory is null)
        {
            yield return CiEvents.Error(
                "validate",
                error ?? "Unable to resolve the working directory.");
            yield break;
        }

        yield return CiEvents.Phase(
            "start",
            $"Starting {ToolName} in {workingDirectory}");

        await foreach (var streamEvent in ExecuteCoreAsync(
            request,
            workingDirectory,
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    protected abstract string ToolName { get; }

    protected abstract IAsyncEnumerable<CiStreamEvent> ExecuteCoreAsync(
        TRequest request,
        string workingDirectory,
        CancellationToken cancellationToken);

    protected virtual void ValidateRequest(TRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RelativePath);
    }

    protected IAsyncEnumerable<CiStreamEvent> RunProcessStreamingAsync(
        string phase,
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        processRunner.RunStreamingAsync(
            phase,
            command,
            arguments,
            workingDirectory,
            cancellationToken);
}
