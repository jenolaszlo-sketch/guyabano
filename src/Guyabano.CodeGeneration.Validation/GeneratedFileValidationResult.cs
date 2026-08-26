namespace Guyabano.CodeGeneration.Validation;

public sealed record GeneratedFileValidationResult(
    string Path,
    bool WasValidated,
    IReadOnlyList<FileValidationDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(
        diagnostic => diagnostic.Severity != FileValidationSeverity.Error);
}
