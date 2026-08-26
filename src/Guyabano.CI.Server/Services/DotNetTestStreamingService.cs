using Microsoft.Extensions.Options;
using Guyabano.CI.Contracts;
using System.Runtime.CompilerServices;

namespace Guyabano.CI.Server.Services;

public sealed class DotNetTestStreamingService(
    IOptions<CiServerOptions> options,
    SafePathResolver safePathResolver,
    ProcessRunner processRunner,
    ProjectTargetResolver targetResolver)
    : ProcessStreamingServiceBase<CiTestRequest>(
        safePathResolver,
        processRunner)
{
    protected override string ToolName => "dotnet test";

    protected override async IAsyncEnumerable<CiStreamEvent> ExecuteCoreAsync(
        CiTestRequest request,
        string workingDirectory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? target = null;
        string? targetError = null;

        try
        {
            target = targetResolver.Resolve(
                workingDirectory,
                request.ProjectOrSolutionFile);
        }
        catch (Exception exception)
        {
            targetError = exception.Message;
        }

        if (targetError is not null || target is null)
        {
            yield return CiEvents.Error(
                "test",
                targetError ?? "Unable to resolve the test target.");
            yield break;
        }

        var arguments = new List<string>
        {
            "test",
            target,
            "--nologo"
        };

        if (request.NoBuild)
        {
            arguments.Add("--no-build");
        }

        if (request.NoRestore)
        {
            arguments.Add("--no-restore");
        }

        var exitCode = -1;

        await foreach (var streamEvent in RunProcessStreamingAsync(
            "test",
            options.Value.DotNetCommand,
            arguments,
            workingDirectory,
            cancellationToken))
        {
            yield return streamEvent;

            if (streamEvent.Type == "process_result" &&
                streamEvent.ExitCode is { } code)
            {
                exitCode = code;
            }
        }

        yield return CiEvents.Result(
            "test-result",
            new { target },
            exitCode == 0,
            exitCode);
    }
}
