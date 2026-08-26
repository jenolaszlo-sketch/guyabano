using Microsoft.Extensions.Logging;
using Guyabano.CodeGeneration.Validation;
using Penghou.Baize;
using Penghou.Baize.Router;
using Guyabano.Llm.Prompting;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;

namespace Guyabano.Llm.CodeGeneration;

internal sealed class CodeGenerationService(
    ILlmRouter llmRouter,
    IPromptBuilder<CodeGenerationPromptContext> promptBuilder,
    IPromptBuilder<CodeGenerationTaskPromptContext> taskPromptBuilder,
    ILlmResponseNormalizer responseNormalizer,
    ICodeGenerationResultParser parser,
    IGeneratedFileValidationPipeline validationPipeline,
    ICodeEmitter codeEmitter,
    ILogger<CodeGenerationService> logger)
    : ICodeGenerationService,
      ICodeGenerationTaskService
{
    private const string EmitFilesToolName = "emit_files";
    private const string EmitTaskFilesToolName = "emit_task_files";

    public async Task<CodeGenerationOutcome> GenerateAndEmitAsync(
        string task,
        string outputRoot,
        string model,
        string projectName,
        string? rootNamespace = null,
        string targetFramework = "net10.0",
        int maxTokens = 8000,
        CancellationToken cancellationToken = default)
    {
        var resultTool = CreateResultTool(
            EmitFilesToolName,
            "Emits the final generated files. Call this exactly once when generation is complete.");
        var request = await promptBuilder.BuildAsync(
            new CodeGenerationPromptContext(
                Task: task,
                ResultToolName: resultTool.Name,
                ProjectName: projectName,
                RootNamespace: string.IsNullOrWhiteSpace(rootNamespace)
                    ? projectName
                    : rootNamespace,
                TargetFramework: targetFramework,
                Tools: [resultTool],
                MaxTokens: maxTokens,
                Temperature: 0.1),
            cancellationToken);

        return await ExecuteAsync(
            request,
            resultTool.Name,
            outputRoot,
            model,
            result => CreateMissingArtifactsError(result, projectName),
            emitWhenContractInvalid: true,
            invalidContractFailure: CodeGenerationFailure.IncompleteProject,
            failOnFileValidation: false,
            failOnNoChanges: false,
            cancellationToken);
    }

    public async Task<CodeGenerationOutcome> GenerateAndEmitAsync(
        CodeGenerationTaskContext task,
        string outputRoot,
        string model,
        int maxTokens = 8000,
        CancellationToken cancellationToken = default)
    {
        var resultTool = CreateResultTool(
            EmitTaskFilesToolName,
            "Emits every file created or changed by the assigned task. Call this exactly once when the task is complete.");
        var request = await taskPromptBuilder.BuildAsync(
            new CodeGenerationTaskPromptContext(
                task,
                resultTool.Name,
                [resultTool],
                maxTokens,
                Temperature: 0.1),
            cancellationToken);

        return await ExecuteAsync(
            request,
            resultTool.Name,
            outputRoot,
            model,
            result => GeneratedFileScopeValidator.Validate(
                result,
                task.ProjectDirectory,
                task.ProjectPath,
                task.SolutionPath,
                task.AllowBuildArtifacts
                    ? task.Artifacts?.Select(artifact => artifact.Path)
                        .ToArray()
                    : null),
            emitWhenContractInvalid: false,
            invalidContractFailure: CodeGenerationFailure.OutOfScopeFiles,
            failOnFileValidation: true,
            failOnNoChanges: task.AllowBuildArtifacts && task.Retry is not null,
            cancellationToken);
    }

    private async Task<CodeGenerationOutcome> ExecuteAsync(
        LlmRequest request,
        string resultToolName,
        string outputRoot,
        string model,
        Func<CodeGenerationResult, string?> contractErrorFactory,
        bool emitWhenContractInvalid,
        CodeGenerationFailure invalidContractFailure,
        bool failOnFileValidation,
        bool failOnNoChanges,
        CancellationToken cancellationToken)
    {
        var response = await llmRouter.CompleteStreamingAsync(
            model,
            request,
            cancellationToken: cancellationToken);

        if (response is null)
        {
            const string error = "The LLM returned no response.";
            LogFailure(model, CodeGenerationFailure.NoResponse, error);
            return CodeGenerationOutcome.Failed(
                CodeGenerationFailure.NoResponse,
                error,
                model);
        }

        var normalizedResponse = await responseNormalizer.NormalizeAsync(
            response,
            request.Tools.ToArray(),
            cancellationToken);
        var resultToolCall = normalizedResponse.ToolCalls?
            .FirstOrDefault(toolCall => toolCall.Name.Equals(
                resultToolName,
                StringComparison.Ordinal));
        var jsonWasRepaired = resultToolCall?.JsonWasRepaired ?? false;
        var jsonRepairAttempts = resultToolCall?.JsonRepairAttempts ?? [];
        var result = parser.Parse(normalizedResponse, resultToolName);

        if (!result.Succeeded)
        {
            var failure = MapFailure(result.Failure);
            var error = result.Error ?? "Tool-call parsing failed.";
            LogFailure(model, failure, error);
            return CodeGenerationOutcome.Failed(
                failure,
                error,
                model,
                jsonWasRepaired,
                jsonRepairAttempts,
                usage: response.Usage,
                diagnostics: response.Diagnostics,
                finishReason: response.FinishReason);
        }

        if (result.Value is null)
        {
            const string error =
                "Tool-call parsing succeeded without a code-generation result.";
            LogFailure(
                model,
                CodeGenerationFailure.DeserializationFailed,
                error);
            return CodeGenerationOutcome.Failed(
                CodeGenerationFailure.DeserializationFailed,
                error,
                model,
                jsonWasRepaired,
                jsonRepairAttempts,
                usage: response.Usage,
                diagnostics: response.Diagnostics,
                finishReason: response.FinishReason);
        }

        if (result.Value.Files.Count == 0)
        {
            const string error =
                "The code-generation result contained no files.";
            LogFailure(model, CodeGenerationFailure.EmptyResult, error);
            return CodeGenerationOutcome.Failed(
                CodeGenerationFailure.EmptyResult,
                error,
                model,
                jsonWasRepaired,
                jsonRepairAttempts,
                usage: response.Usage,
                diagnostics: response.Diagnostics,
                finishReason: response.FinishReason);
        }

        var validation = await validationPipeline.ValidateAsync(
            result.Value.Files.Select(file =>
                new GeneratedFileContent(file.Path, file.Content)),
            cancellationToken);
        LogValidationResult(validation, model);

        var contractError = contractErrorFactory(result.Value);
        if (contractError is not null && !emitWhenContractInvalid)
        {
            LogFailure(model, invalidContractFailure, contractError);
            return CodeGenerationOutcome.Failed(
                invalidContractFailure,
                contractError,
                model,
                jsonWasRepaired,
                jsonRepairAttempts,
                fileValidation: validation,
                usage: response.Usage,
                diagnostics: response.Diagnostics,
                finishReason: response.FinishReason);
        }

        try
        {
            var emission = await codeEmitter.EmitAsync(
                result.Value,
                outputRoot,
                cancellationToken);

            if (failOnNoChanges && emission.WrittenFiles.Count == 0)
            {
                const string error =
                    "The build-artifact repair emitted no content changes.";
                LogFailure(model, CodeGenerationFailure.NoChanges, error);
                return CodeGenerationOutcome.Failed(
                    CodeGenerationFailure.NoChanges,
                    error,
                    model,
                    jsonWasRepaired,
                    jsonRepairAttempts,
                    emission,
                    validation,
                    response.Usage,
                    response.Diagnostics,
                    response.FinishReason);
            }

            if (contractError is not null)
            {
                LogFailure(model, invalidContractFailure, contractError);
                return CodeGenerationOutcome.Failed(
                    invalidContractFailure,
                    contractError,
                    model,
                    jsonWasRepaired,
                    jsonRepairAttempts,
                    emission,
                    validation,
                    response.Usage,
                    response.Diagnostics,
                    response.FinishReason);
            }

            if (failOnFileValidation && !validation.IsValid)
            {
                var error =
                    $"Task output failed syntax validation with {validation.Diagnostics.Count} diagnostic(s).";
                LogFailure(
                    model,
                    CodeGenerationFailure.FileValidationFailed,
                    error);
                return CodeGenerationOutcome.Failed(
                    CodeGenerationFailure.FileValidationFailed,
                    error,
                    model,
                    jsonWasRepaired,
                    jsonRepairAttempts,
                    emission,
                    validation,
                    response.Usage,
                    response.Diagnostics,
                    response.FinishReason);
            }

            return CodeGenerationOutcome.Success(
                emission,
                model,
                jsonWasRepaired,
                jsonRepairAttempts,
                validation,
                response.Usage,
                response.Diagnostics,
                response.FinishReason);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Code generation failed for model {Model}: {Failure}.",
                model,
                CodeGenerationFailure.EmissionFailed);
            return CodeGenerationOutcome.Failed(
                CodeGenerationFailure.EmissionFailed,
                exception.Message,
                model,
                jsonWasRepaired,
                jsonRepairAttempts,
                fileValidation: validation,
                usage: response.Usage,
                diagnostics: response.Diagnostics,
                finishReason: response.FinishReason);
        }
    }

    private void LogValidationResult(
        GeneratedFilesValidationResult validation,
        string model)
    {
        if (validation.IsValid)
        {
            logger.LogInformation(
                "Validated {ValidatedFileCount} generated files for model {Model}; {UnvalidatedFileCount} files had no registered validator.",
                validation.ValidatedFiles.Count,
                model,
                validation.UnvalidatedFiles.Count);
            return;
        }

        logger.LogWarning(
            "Generated output from model {Model} contains {DiagnosticCount} file validation error(s). Files will still be emitted for inspection.",
            model,
            validation.Diagnostics.Count);
        foreach (var diagnostic in validation.Diagnostics)
        {
            logger.LogWarning(
                "Generated file validation failed: {FilePath}({Line},{Column}) {Code} [{Validator}]: {Message}",
                diagnostic.FilePath,
                diagnostic.Line,
                diagnostic.Column,
                diagnostic.Code,
                diagnostic.Validator,
                diagnostic.Message);
        }
    }

    private void LogFailure(
        string model,
        CodeGenerationFailure failure,
        string error) =>
        logger.LogWarning(
            "Code generation failed for model {Model}: {Failure}. {Error}",
            model,
            failure,
            error);

    private static CodeGenerationFailure MapFailure(
        ToolCallParseFailure failure) =>
        failure switch
        {
            ToolCallParseFailure.MissingToolCall =>
                CodeGenerationFailure.MissingToolCall,
            ToolCallParseFailure.EmptyArguments or
            ToolCallParseFailure.InvalidJson =>
                CodeGenerationFailure.InvalidToolArguments,
            ToolCallParseFailure.TruncatedResponse =>
                CodeGenerationFailure.TruncatedResponse,
            ToolCallParseFailure.SchemaValidationFailed =>
                CodeGenerationFailure.SchemaValidationFailed,
            ToolCallParseFailure.DeserializationFailed =>
                CodeGenerationFailure.DeserializationFailed,
            _ => CodeGenerationFailure.InvalidToolArguments
        };

    private static string? CreateMissingArtifactsError(
        CodeGenerationResult result,
        string projectName)
    {
        var emittedPaths = result.Files
            .Select(file => file.Path.Replace('\\', '/').TrimStart('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredPaths = new[]
        {
            $"{projectName}.sln",
            $"src/{projectName}/{projectName}.csproj",
            $"tests/{projectName}.Tests/{projectName}.Tests.csproj"
        };
        var missing = requiredPaths
            .Where(path => !emittedPaths.Contains(path))
            .ToArray();

        return missing.Length == 0
            ? null
            : $"The result omitted required project artifacts: {string.Join(", ", missing)}.";
    }

    private static LlmTool CreateResultTool(
        string name,
        string description) =>
        new(
            name,
            description,
            JsonSchemaGenerator.GenerateSchemaJson<CodeGenerationResult>());
}
