namespace Guyabano.CodeGeneration.Validation;

public interface IGeneratedFileValidationPipeline
{
    ValueTask<GeneratedFilesValidationResult> ValidateAsync(
        IEnumerable<GeneratedFileContent> files,
        CancellationToken cancellationToken = default);
}
