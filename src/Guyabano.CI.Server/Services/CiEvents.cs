using Guyabano.CI.Contracts;

namespace Guyabano.CI.Server.Services;

internal static class CiEvents
{
    public static CiStreamEvent Phase(string phase, string message) =>
        new("phase", phase, Message: message);

    public static CiStreamEvent StandardOutput(
        string phase,
        string message) =>
        new("log", phase, "stdout", message);

    public static CiStreamEvent StandardError(
        string phase,
        string message) =>
        new("log", phase, "stderr", message);

    public static CiStreamEvent Error(string phase, string message) =>
        new(
            "error",
            phase,
            Message: message,
            Success: false);

    public static CiStreamEvent ProcessResult(
        string phase,
        CiProcessResult result) =>
        new(
            "process_result",
            phase,
            Data: result,
            Success: result.Success,
            ExitCode: result.ExitCode);

    public static CiStreamEvent Result(
        string phase,
        object data,
        bool success,
        int? exitCode = null) =>
        new(
            "result",
            phase,
            Data: data,
            Success: success,
            ExitCode: exitCode);
}
