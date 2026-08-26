namespace Guyabano.CodeGeneration.Validation;

public sealed record FileValidationResult(
    IReadOnlyList<FileValidationDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(
        diagnostic => diagnostic.Severity != FileValidationSeverity.Error);

    public static FileValidationResult Valid { get; } = new([]);
}
