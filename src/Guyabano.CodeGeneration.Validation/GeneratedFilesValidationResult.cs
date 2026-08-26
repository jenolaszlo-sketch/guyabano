namespace Guyabano.CodeGeneration.Validation;

public sealed record GeneratedFilesValidationResult(
    IReadOnlyList<GeneratedFileValidationResult> Files)
{
    public IReadOnlyList<FileValidationDiagnostic> Diagnostics => Files
        .SelectMany(file => file.Diagnostics)
        .ToArray();

    public IReadOnlyList<string> ValidatedFiles => Files
        .Where(file => file.WasValidated)
        .Select(file => file.Path)
        .ToArray();

    public IReadOnlyList<string> UnvalidatedFiles => Files
        .Where(file => !file.WasValidated)
        .Select(file => file.Path)
        .ToArray();

    public bool IsValid => Files.All(file => file.IsValid);
}
