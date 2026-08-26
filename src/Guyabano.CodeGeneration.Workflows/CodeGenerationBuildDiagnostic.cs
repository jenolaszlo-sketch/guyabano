namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationBuildDiagnostic(
    string Tool,
    string Code,
    string Severity,
    string Message,
    string? FilePath = null,
    string? ProjectPath = null,
    int? Line = null,
    int? Column = null);
