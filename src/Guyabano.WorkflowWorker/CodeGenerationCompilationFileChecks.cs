using Guyabano.CI.Contracts;
using Guyabano.Messaging;

namespace Guyabano.WorkflowWorker;

internal static class CodeGenerationCompilationFileChecks
{
    private static readonly HashSet<string> CompilableExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets"
        };

    public static IReadOnlyList<WorkflowGeneratedFileChecks> CreateRunning(
        IReadOnlyList<string> paths) =>
        paths.Select(path => Create(
            path,
            IsCompilable(path)
                ? WorkflowFileCheckStatus.Running
                : WorkflowFileCheckStatus.NotApplicable,
            [])).ToArray();

    public static IReadOnlyList<WorkflowGeneratedFileChecks> CreateCompleted(
        IReadOnlyList<string> paths,
        bool succeeded,
        IReadOnlyList<CiDiagnostic> diagnostics)
    {
        return paths.Select(path =>
        {
            if (!IsCompilable(path))
            {
                return Create(
                    path,
                    WorkflowFileCheckStatus.NotApplicable,
                    []);
            }

            var fileDiagnostics = diagnostics
                .Where(diagnostic => Matches(path, diagnostic))
                .Select(MapDiagnostic)
                .ToArray();
            var status = fileDiagnostics.Any(diagnostic =>
                    diagnostic.Severity == WorkflowDiagnosticSeverity.Error)
                ? WorkflowFileCheckStatus.Failed
                : fileDiagnostics.Any(diagnostic =>
                    diagnostic.Severity == WorkflowDiagnosticSeverity.Warning)
                    ? WorkflowFileCheckStatus.Warning
                    : succeeded
                        ? WorkflowFileCheckStatus.Passed
                        : WorkflowFileCheckStatus.NotRun;

            return Create(path, status, fileDiagnostics);
        }).ToArray();
    }

    public static WorkflowDiagnostic MapDiagnostic(
        CiDiagnostic diagnostic)
    {
        var details = new List<string> { $"Tool: {diagnostic.Tool}" };

        if (!string.IsNullOrWhiteSpace(diagnostic.ProjectPath))
        {
            details.Add($"Project: {diagnostic.ProjectPath}");
        }

        if (diagnostic.Line is not null)
        {
            details.Add(diagnostic.Column is null
                ? $"Line: {diagnostic.Line}"
                : $"Location: line {diagnostic.Line}, column {diagnostic.Column}");
        }

        return new WorkflowDiagnostic(
            diagnostic.Severity == CiDiagnosticSeverity.Error
                ? WorkflowDiagnosticSeverity.Error
                : WorkflowDiagnosticSeverity.Warning,
            diagnostic.Code,
            diagnostic.Message,
            details);
    }

    private static WorkflowGeneratedFileChecks Create(
        string path,
        WorkflowFileCheckStatus status,
        IReadOnlyList<WorkflowDiagnostic> diagnostics) =>
        new(path,
        [
            new WorkflowFileCheck(
                WorkflowFileCheckKind.Compilation,
                status,
                diagnostics)
        ]);

    private static bool IsCompilable(string path) =>
        CompilableExtensions.Contains(Path.GetExtension(path));

    private static bool Matches(string path, CiDiagnostic diagnostic) =>
        PathEquals(path, diagnostic.FilePath) ||
        PathEquals(path, diagnostic.ProjectPath);

    private static bool PathEquals(string left, string? right) =>
        right is not null &&
        Normalize(left).Equals(
            Normalize(right),
            StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/');
}
