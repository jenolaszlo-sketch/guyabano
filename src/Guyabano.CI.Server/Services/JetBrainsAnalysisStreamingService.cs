using Microsoft.Extensions.Options;
using Guyabano.CI.Contracts;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Guyabano.CI.Server.Services;

public sealed class JetBrainsAnalysisStreamingService(
    IOptions<CiServerOptions> options,
    SafePathResolver safePathResolver,
    ProcessRunner processRunner,
    ProjectTargetResolver targetResolver)
    : ProcessStreamingServiceBase<CiJetBrainsAnalysisRequest>(
        safePathResolver,
        processRunner)
{
    private const string ReportFileName = "inspectcode.json";

    protected override string ToolName => "JetBrains inspectcode";

    protected override async IAsyncEnumerable<CiStreamEvent> ExecuteCoreAsync(
        CiJetBrainsAnalysisRequest request,
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
                "inspectcode",
                targetError ?? "Unable to resolve the inspection target.");
            yield break;
        }

        var reportPath = Path.Combine(
            workingDirectory,
            ReportFileName);

        string? cleanupError = null;

        try
        {
            File.Delete(reportPath);
        }
        catch (Exception exception)
        {
            cleanupError = exception.Message;
        }

        if (cleanupError is not null)
        {
            yield return CiEvents.Error(
                "inspectcode-report-cleanup",
                cleanupError);
            yield break;
        }

        var exitCode = -1;

        await foreach (var streamEvent in RunProcessStreamingAsync(
            "inspectcode",
            options.Value.JetBrainsCommand,
            [
                "inspectcode",
                target,
                $"--output={reportPath}",
                "--format=Json",
                "--no-updates"
            ],
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

        JsonElement? report = null;
        string? reportError = null;

        try
        {
            if (File.Exists(reportPath))
            {
                var content = await File.ReadAllTextAsync(
                    reportPath,
                    cancellationToken);
                using var document = JsonDocument.Parse(content);
                report = document.RootElement.Clone();
            }
            else
            {
                reportError = $"{ReportFileName} was not produced.";
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            reportError = exception.Message;
        }
        finally
        {
            try
            {
                File.Delete(reportPath);
            }
            catch (Exception exception)
            {
                reportError ??=
                    $"The report could not be deleted: {exception.Message}";
            }
        }

        if (reportError is not null)
        {
            yield return CiEvents.Error(
                "inspectcode-report",
                reportError);
        }

        if (report is not null)
        {
            yield return new CiStreamEvent(
                Type: "report",
                Phase: "inspectcode-report",
                Message: $"{ReportFileName} loaded.",
                Data: report,
                Success: exitCode == 0,
                ExitCode: exitCode);
        }

        yield return CiEvents.Result(
            "inspectcode-result",
            new
            {
                target,
                reportAvailable = report is not null
            },
            exitCode == 0 && report is not null,
            exitCode);
    }
}
