using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Guyabano.CodeGeneration.Validation.Validators;

public sealed class CSharpSyntaxValidator : IGeneratedFileValidator
{
    public string Name => "csharp-syntax";

    public ValueTask<FileValidationResult> ValidateAsync(
        GeneratedFileContent file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var syntaxTree = CSharpSyntaxTree.ParseText(
            file.Content,
            path: file.Path,
            cancellationToken: cancellationToken);

        var diagnostics = syntaxTree
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => CreateDiagnostic(file.Path, diagnostic))
            .ToArray();

        return ValueTask.FromResult(
            diagnostics.Length == 0
                ? FileValidationResult.Valid
                : new FileValidationResult(diagnostics));
    }

    private FileValidationDiagnostic CreateDiagnostic(
        string filePath,
        Diagnostic diagnostic)
    {
        int? line = null;
        int? column = null;

        if (diagnostic.Location.IsInSource)
        {
            var position = diagnostic.Location
                .GetLineSpan()
                .StartLinePosition;

            line = position.Line + 1;
            column = position.Character + 1;
        }

        return new FileValidationDiagnostic(
            Validator: Name,
            Code: diagnostic.Id,
            Severity: FileValidationSeverity.Error,
            Message: diagnostic.GetMessage(),
            FilePath: filePath,
            Line: line,
            Column: column);
    }
}
