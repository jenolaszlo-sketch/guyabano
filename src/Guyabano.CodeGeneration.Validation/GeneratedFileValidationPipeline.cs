namespace Guyabano.CodeGeneration.Validation;

internal sealed class GeneratedFileValidationPipeline(
    IEnumerable<GeneratedFileValidatorRegistration> registrations)
    : IGeneratedFileValidationPipeline
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IGeneratedFileValidator>>
        validatorsByExtension = registrations
            .GroupBy(
                registration => registration.Extension,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IGeneratedFileValidator>)group
                    .Select(registration => registration.Validator)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

    public async ValueTask<GeneratedFilesValidationResult> ValidateAsync(
        IEnumerable<GeneratedFileContent> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        var results = new List<GeneratedFileValidationResult>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(file.Path);

            if (string.IsNullOrEmpty(extension) ||
                !validatorsByExtension.TryGetValue(extension, out var validators))
            {
                results.Add(
                    new GeneratedFileValidationResult(
                        file.Path,
                        WasValidated: false,
                        Diagnostics: []));
                continue;
            }

            var diagnostics = new List<FileValidationDiagnostic>();

            foreach (var validator in validators)
            {
                var result = await validator.ValidateAsync(
                    file,
                    cancellationToken);

                diagnostics.AddRange(result.Diagnostics);
            }

            results.Add(
                new GeneratedFileValidationResult(
                    file.Path,
                    WasValidated: true,
                    diagnostics));
        }

        return new GeneratedFilesValidationResult(results);
    }
}
