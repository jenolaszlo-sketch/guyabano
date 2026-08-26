using Guyabano.CI.Contracts;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Guyabano.CI.Server.Services;

public sealed class ProcessRunner
{
    public async IAsyncEnumerable<CiStreamEvent> RunStreamingAsync(
        string phase,
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var channel = Channel.CreateUnbounded<CiStreamEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        var runTask = RunCoreAsync(
            phase,
            command,
            arguments,
            workingDirectory,
            channel.Writer,
            cancellationToken);

        await foreach (var streamEvent in channel.Reader.ReadAllAsync(
            cancellationToken))
        {
            yield return streamEvent;
        }

        await runTask;
    }

    private static async Task RunCoreAsync(
        string phase,
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        ChannelWriter<CiStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        try
        {
            await writer.WriteAsync(
                CiEvents.Phase(
                    phase,
                    $"Running command: {command} {string.Join(' ', arguments)}"),
                cancellationToken);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Failed to start process: {command}");
            }

            using var cancellationRegistration = cancellationToken.Register(
                () => TryKill(process));
            var outputTask = ReadLinesAsync(
                process.StandardOutput,
                standardOutput,
                line => CiEvents.StandardOutput(phase, line),
                writer,
                cancellationToken);
            var errorTask = ReadLinesAsync(
                process.StandardError,
                standardError,
                line => CiEvents.StandardError(phase, line),
                writer,
                cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);

            var result = new CiProcessResult(
                command,
                arguments,
                workingDirectory,
                process.ExitCode,
                standardOutput.ToString(),
                standardError.ToString());

            await writer.WriteAsync(
                CiEvents.ProcessResult(phase, result),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await writer.WriteAsync(
                CiEvents.Error(phase, $"Command canceled: {command}"),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await writer.WriteAsync(
                CiEvents.Error(phase, exception.Message),
                CancellationToken.None);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        StringBuilder output,
        Func<string, CiStreamEvent> createEvent,
        ChannelWriter<CiStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            output.AppendLine(line);
            await writer.WriteAsync(createEvent(line), cancellationToken);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
