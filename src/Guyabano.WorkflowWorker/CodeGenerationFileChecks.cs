using Guyabano.CodeGeneration.Validation;
using Guyabano.Llm.CodeGeneration;
using Guyabano.Messaging;

namespace Guyabano.WorkflowWorker;

internal static class CodeGenerationFileChecks
{
    public static IReadOnlyList<WorkflowGeneratedFileChecks> Create(
        CodeGenerationOutcome outcome)
    {
        if (outcome.FileValidation is null)
        {
            return [];
        }

        return outcome.FileValidation.Files
            .Select(CreateFileChecks)
            .ToArray();
    }

    private static WorkflowGeneratedFileChecks CreateFileChecks(
        GeneratedFileValidationResult file)
    {
        var diagnostics = file.Diagnostics
            .Select(MapDiagnostic)
            .ToArray();

        var syntaxStatus = !file.WasValidated
            ? WorkflowFileCheckStatus.NotApplicable
            : diagnostics.Any(diagnostic =>
                diagnostic.Severity == WorkflowDiagnosticSeverity.Error)
                ? WorkflowFileCheckStatus.Failed
                : diagnostics.Any(diagnostic =>
                    diagnostic.Severity == WorkflowDiagnosticSeverity.Warning)
                    ? WorkflowFileCheckStatus.Warning
                    : WorkflowFileCheckStatus.Passed;

        return new WorkflowGeneratedFileChecks(
            file.Path,
            [
                new WorkflowFileCheck(
                    WorkflowFileCheckKind.Syntax,
                    syntaxStatus,
                    diagnostics),
                new WorkflowFileCheck(
                    WorkflowFileCheckKind.Compilation,
                    WorkflowFileCheckStatus.NotRun,
                    [])
            ]);
    }

    private static WorkflowDiagnostic MapDiagnostic(
        FileValidationDiagnostic diagnostic)
    {
        var details = new List<string>
        {
            $"Validator: {diagnostic.Validator}"
        };

        if (diagnostic.Line is not null)
        {
            details.Add(
                diagnostic.Column is null
                    ? $"Line: {diagnostic.Line}"
                    : $"Location: line {diagnostic.Line}, column {diagnostic.Column}");
        }

        return new WorkflowDiagnostic(
            MapSeverity(diagnostic.Severity),
            diagnostic.Code,
            diagnostic.Message,
            details);
    }

    private static WorkflowDiagnosticSeverity MapSeverity(
        FileValidationSeverity severity) => severity switch
        {
            FileValidationSeverity.Error =>
                WorkflowDiagnosticSeverity.Error,
            FileValidationSeverity.Warning =>
                WorkflowDiagnosticSeverity.Warning,
            _ => WorkflowDiagnosticSeverity.Information
        };
}
