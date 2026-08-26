using Penghou.Baize;
using Guyabano.CodeGeneration.Validation;

namespace Guyabano.Llm.CodeGeneration;

public sealed record CodeGenerationOutcome(
    bool Succeeded,
    CodeGenerationFailure Failure,
    string? Error,
    string Model,
    bool JsonWasRepaired,
    IReadOnlyList<LlmRepairAttempt> JsonRepairAttempts,
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> SkippedFiles)
{
    public LlmUsage? Usage { get; init; }

    public LlmProviderDiagnostics? Diagnostics
    {
        get;
        init;
    }

    public GeneratedFilesValidationResult? FileValidation
    {
        get;
        init;
    }

    public string? FinishReason { get; init; }

    internal static CodeGenerationOutcome Success(
        CodeEmitResult emission,
        string model,
        bool jsonWasRepaired,
        IReadOnlyList<LlmRepairAttempt> jsonRepairAttempts,
        GeneratedFilesValidationResult? fileValidation = null,
        LlmUsage? usage = null,
        LlmProviderDiagnostics? diagnostics = null,
        string? finishReason = null) =>
        new(
            Succeeded: true,
            Failure: CodeGenerationFailure.None,
            Error: null,
            Model: model,
            JsonWasRepaired: jsonWasRepaired,
            JsonRepairAttempts: jsonRepairAttempts,
            WrittenFiles: emission.WrittenFiles,
            SkippedFiles: emission.SkippedFiles)
        {
            Usage = usage,
            Diagnostics = diagnostics,
            FileValidation = fileValidation,
            FinishReason = finishReason
        };

    internal static CodeGenerationOutcome Failed(
        CodeGenerationFailure failure,
        string error,
        string model,
        bool jsonWasRepaired = false,
        IReadOnlyList<LlmRepairAttempt>? jsonRepairAttempts = null,
        CodeEmitResult? emission = null,
        GeneratedFilesValidationResult? fileValidation = null,
        LlmUsage? usage = null,
        LlmProviderDiagnostics? diagnostics = null,
        string? finishReason = null) =>
        new(
            Succeeded: false,
            Failure: failure,
            Error: error,
            Model: model,
            JsonWasRepaired: jsonWasRepaired,
            JsonRepairAttempts: jsonRepairAttempts ?? [],
            WrittenFiles: emission?.WrittenFiles ?? [],
            SkippedFiles: emission?.SkippedFiles ?? [])
        {
            Usage = usage,
            Diagnostics = diagnostics,
            FileValidation = fileValidation,
            FinishReason = finishReason
        };
}
