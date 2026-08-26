namespace Guyabano.CodeGeneration.Validation;

public sealed record FileValidationDiagnostic(
    string Validator,
    string Code,
    FileValidationSeverity Severity,
    string Message,
    string FilePath,
    int? Line = null,
    int? Column = null);
