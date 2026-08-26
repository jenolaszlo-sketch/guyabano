using Microsoft.Extensions.Logging;
using Penghou.Baize;
using Penghou.Baize.Router;
using Guyabano.Llm.Prompting;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;

namespace Guyabano.CodeGeneration.Planning;

internal sealed class CodeGenerationTaskDecompositionService(
    ILlmRouter llmRouter,
    IPromptBuilder<CodeGenerationDecompositionPromptContext> promptBuilder,
    ILlmResponseNormalizer responseNormalizer,
    ICodeGenerationTaskDecompositionParser parser,
    ILogger<CodeGenerationTaskDecompositionService> logger)
    : ICodeGenerationTaskDecompositionService
{
    internal const string ToolName = "return_task_decomposition";

    public async Task<CodeGenerationDecompositionOutcome> DecomposeAsync(
        ComponentWorkContext workContext,
        string model,
        int maxTokens = 8000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var parent = workContext.ParentTask;
        var tool = new LlmTool(
            ToolName,
            "Returns execution-ready leaf tasks or explicit architecture gaps for one immutable parent task.",
            JsonSchemaGenerator.GenerateSchemaJson<
                CodeGenerationTaskDecomposition>());
        var request = await promptBuilder.BuildAsync(
            new CodeGenerationDecompositionPromptContext(
                workContext,
                tool.Name,
                [tool],
                maxTokens),
            cancellationToken);
        var response = await llmRouter.CompleteStreamingAsync(
            model,
            request,
            cancellationToken: cancellationToken);

        if (response is null)
            return Failed(
                PlanningFailure.NoResponse,
                "The LLM returned no decomposition response.",
                model);

        var normalized = await responseNormalizer.NormalizeAsync(
            response,
            request.Tools.ToArray(),
            cancellationToken);
        var call = normalized.ToolCalls?.FirstOrDefault(item =>
            item.Name.Equals(ToolName, StringComparison.Ordinal));
        var repaired = call?.JsonWasRepaired ?? false;
        var attempts = call?.JsonRepairAttempts ?? [];
        var parsed = parser.Parse(normalized);

        if (!parsed.Succeeded || parsed.Value is null)
        {
            return Failed(
                MapFailure(parsed.Failure),
                parsed.Error ?? "The decomposition tool call could not be parsed.",
                model,
                repaired,
                attempts,
                response);
        }

        var errors = CodeGenerationTaskDecompositionValidator.Validate(
            workContext,
            parsed.Value,
            workContext.ResolvedDependencies);
        if (errors.Count > 0)
        {
            var error =
                $"Task decomposition validation failed: {string.Join(" ", errors)}";
            logger.LogWarning(
                "Task {TaskId} decomposition from {Model} was invalid: {Error}",
                parent.Id,
                model,
                error);
            return Failed(
                PlanningFailure.InvalidPlan,
                error,
                model,
                repaired,
                attempts,
                response);
        }

        logger.LogInformation(
            "Task {TaskId} decomposed into {LeafCount} leaf task(s) using {Model} with status {Status}.",
            parent.Id,
            parsed.Value.LeafTasks.Count,
            model,
            parsed.Value.Status);

        return new(
            true,
            PlanningFailure.None,
            null,
            model,
            parsed.Value,
            repaired,
            attempts)
        {
            Usage = response.Usage,
            Diagnostics = response.Diagnostics,
            FinishReason = response.FinishReason
        };
    }

    private static PlanningFailure MapFailure(
        ToolCallParseFailure failure) => failure switch
        {
            ToolCallParseFailure.MissingToolCall =>
                PlanningFailure.MissingToolCall,
            ToolCallParseFailure.EmptyArguments or
            ToolCallParseFailure.InvalidJson =>
                PlanningFailure.InvalidToolArguments,
            ToolCallParseFailure.TruncatedResponse =>
                PlanningFailure.TruncatedResponse,
            ToolCallParseFailure.SchemaValidationFailed =>
                PlanningFailure.SchemaValidationFailed,
            ToolCallParseFailure.DeserializationFailed =>
                PlanningFailure.DeserializationFailed,
            _ => PlanningFailure.InvalidToolArguments
        };

    private static CodeGenerationDecompositionOutcome Failed(
        PlanningFailure failure,
        string error,
        string model,
        bool repaired = false,
        IReadOnlyList<LlmRepairAttempt>? attempts = null,
        LlmResponse? response = null) =>
        new(
            false,
            failure,
            error,
            model,
            null,
            repaired,
            attempts ?? [])
        {
            Usage = response?.Usage,
            Diagnostics = response?.Diagnostics,
            FinishReason = response?.FinishReason
        };
}
