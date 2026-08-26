using Microsoft.Extensions.Options;
using Guyabano.CI.Contracts;
using System.Runtime.CompilerServices;

namespace Guyabano.CI.Server.Services;

public sealed class DotNetBuildStreamingService(
    IOptions<CiServerOptions> options,
    SafePathResolver safePathResolver,
    ProcessRunner processRunner,
    ProjectTargetResolver targetResolver,
    DotNetDiagnosticParser diagnosticParser)
    : ProcessStreamingServiceBase<CiBuildRequest>(
        safePathResolver,
        processRunner)
{
    protected override string ToolName => "dotnet build";

    protected override async IAsyncEnumerable<CiStreamEvent> ExecuteCoreAsync(
        CiBuildRequest request,
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
                "build",
                targetError ?? "Unable to resolve the build target.");
            yield break;
        }

        var arguments = new List<string>
        {
            "build",
            target,
            "--nologo"
        };

        if (request.NoRestore)
        {
            arguments.Add("--no-restore");
        }

        var exitCode = -1;
        var diagnosticCount = 0;

        await foreach (var streamEvent in RunProcessStreamingAsync(
            "build",
            options.Value.DotNetCommand,
            arguments,
            workingDirectory,
            cancellationToken))
        {
            yield return streamEvent;

            if (streamEvent.Type == "log" &&
                streamEvent.Message is not null &&
                diagnosticParser.TryParse(
                    streamEvent.Message,
                    out var diagnostic) &&
                diagnostic is not null)
            {
                diagnosticCount++;
                yield return new CiStreamEvent(
                    Type: "diagnostic",
                    Phase: "build",
                    Message: diagnostic.Message,
                    Success: diagnostic.Severity !=
                        CiDiagnosticSeverity.Error,
                    Diagnostic: diagnostic);
            }

            if (streamEvent.Type == "process_result" &&
                streamEvent.ExitCode is { } code)
            {
                exitCode = code;
            }
        }

        yield return CiEvents.Result(
            "build-result",
            new { target, diagnosticCount },
            exitCode == 0,
            exitCode);
    }
}
