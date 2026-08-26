using Penghou.Baize;
using Guyabano.Llm.CodeGeneration;
using Guyabano.Llm.Prompting;

namespace Guyabano.WorkflowWorker;

internal static class CodeGenerationRetryContextFactory
{
    private const int MaximumDiagnostics = 40;

    public static CodeGenerationTaskRetryContext Create(
        CodeGenerationOutcome outcome,
        int attempt,
        string model,
        string outputRoot)
    {
        var diagnostics = outcome.JsonRepairAttempts
            .Where(item => !IsUnchanged(item))
            .Select(item => $"JSON repair {item.Name}: {item.Status}")
            .Concat(CreateFileDiagnostics(outcome))
            .Take(MaximumDiagnostics)
            .ToArray();

        return new CodeGenerationTaskRetryContext(
            attempt,
            model,
            outcome.Failure.ToString(),
            outcome.Error,
            diagnostics,
            ToRelativePaths(outputRoot, outcome.WrittenFiles));
    }

    private static IEnumerable<string> CreateFileDiagnostics(
        CodeGenerationOutcome outcome)
    {
        if (outcome.FileValidation is null)
            yield break;

        foreach (var file in outcome.FileValidation.Files)
        {
            foreach (var diagnostic in file.Diagnostics)
            {
                var location = diagnostic.Line is null
                    ? string.Empty
                    : diagnostic.Column is null
                        ? $" line {diagnostic.Line}"
                        : $" line {diagnostic.Line}, column {diagnostic.Column}";
                yield return
                    $"{file.Path}{location} {diagnostic.Code}: {diagnostic.Message}";
            }
        }
    }

    private static IReadOnlyList<string> ToRelativePaths(
        string outputRoot,
        IEnumerable<string> paths)
    {
        var fullRoot = Path.GetFullPath(outputRoot);
        return paths
            .Select(Path.GetFullPath)
            .Select(path => Path.GetRelativePath(fullRoot, path))
            .Where(path => path != ".." &&
                !path.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .Select(path => path.Replace('\\', '/'))
            .ToArray();
    }

    private static bool IsUnchanged(LlmRepairAttempt attempt) =>
        attempt.Status is
            LlmRepairStatus.Skipped or
            LlmRepairStatus.NotApplicable;
}
