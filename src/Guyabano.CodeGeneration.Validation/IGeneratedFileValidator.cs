namespace Guyabano.CodeGeneration.Validation;

public interface IGeneratedFileValidator
{
    string Name { get; }

    ValueTask<FileValidationResult> ValidateAsync(
        GeneratedFileContent file,
        CancellationToken cancellationToken = default);
}
