using System.Xml;
using System.Xml.Linq;

namespace Guyabano.CodeGeneration.Validation.Validators;

public sealed class XmlSyntaxValidator : IGeneratedFileValidator
{
    public string Name => "xml-syntax";

    public ValueTask<FileValidationResult> ValidateAsync(
        GeneratedFileContent file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var textReader = new StringReader(file.Content);
            using var xmlReader = XmlReader.Create(
                textReader,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });

            _ = XDocument.Load(xmlReader, LoadOptions.SetLineInfo);

            return ValueTask.FromResult(FileValidationResult.Valid);
        }
        catch (XmlException exception)
        {
            var diagnostic = new FileValidationDiagnostic(
                Validator: Name,
                Code: "XML001",
                Severity: FileValidationSeverity.Error,
                Message: exception.Message,
                FilePath: file.Path,
                Line: exception.LineNumber > 0
                    ? exception.LineNumber
                    : null,
                Column: exception.LinePosition > 0
                    ? exception.LinePosition
                    : null);

            return ValueTask.FromResult(
                new FileValidationResult([diagnostic]));
        }
    }
}
