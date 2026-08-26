using System.Text.Json;

namespace Guyabano.CodeGeneration.Validation.Validators;

public sealed class JsonSyntaxValidator : IGeneratedFileValidator
{
    public string Name => "json-syntax";

    public ValueTask<FileValidationResult> ValidateAsync(
        GeneratedFileContent file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var _ = JsonDocument.Parse(
                file.Content,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });

            return ValueTask.FromResult(FileValidationResult.Valid);
        }
        catch (JsonException exception)
        {
            var diagnostic = new FileValidationDiagnostic(
                Validator: Name,
                Code: "JSON001",
                Severity: FileValidationSeverity.Error,
                Message: exception.Message,
                FilePath: file.Path,
                Line: ToOneBased(exception.LineNumber),
                Column: ToOneBased(exception.BytePositionInLine));

            return ValueTask.FromResult(
                new FileValidationResult([diagnostic]));
        }
    }

    private static int? ToOneBased(long? position) =>
        position is null || position >= int.MaxValue
            ? null
            : (int)position.Value + 1;
}
