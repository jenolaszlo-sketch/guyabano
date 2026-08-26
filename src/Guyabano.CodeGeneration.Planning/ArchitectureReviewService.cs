using Microsoft.Extensions.Logging;
using Penghou.Baize;
using Penghou.Baize.Router;
using Guyabano.Llm.Prompting;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;

namespace Guyabano.CodeGeneration.Planning;

internal sealed class ArchitectureReviewService(
    ILlmRouter router,
    IPromptBuilder<ArchitectureReviewPromptContext> promptBuilder,
    ILlmStructuredOutputRepairer structuredOutputRepairer,
    ArchitectureReviewParser parser,
    ILogger<ArchitectureReviewService> logger)
    : IArchitectureReviewService
{
    public async Task<ArchitectureReviewOutcome> ReviewAsync(
        CodeGenerationPlan plan,
        int reviewPass,
        string model,
        int maxTokens,
        ArchitectureReview? previousReview = null,
        string? previousFailure = null,
        CancellationToken cancellationToken = default)
    {
        var responseFormat = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<ArchitectureReview>());
        var request = await promptBuilder.BuildAsync(
            new ArchitectureReviewPromptContext(
                plan,
                reviewPass,
                responseFormat,
                maxTokens,
                PreviousReview: previousReview)
            {
                PreviousFailure = previousFailure
            },
            cancellationToken);
        var response = await router.CompleteStreamingAsync(
            model,
            request,
            cancellationToken: cancellationToken);
        if (response is null)
            return Failed(
                PlanningFailure.NoResponse,
                "The architecture reviewer returned no response.",
                model);

        var repaired = await structuredOutputRepairer.RepairAsync(
            response,
            responseFormat,
            cancellationToken);
        var repair = (
            Repaired: repaired.ContentWasRepaired,
            Attempts: (IReadOnlyList<LlmRepairAttempt>)(
                repaired.ContentRepairAttempts ?? []));
        var parsed = parser.Parse(repaired);
        if (!parsed.Succeeded || parsed.Value is null)
            return Failed(
                ArchitecturePlanningFailureMapper.Map(parsed.Failure),
                parsed.Error ?? "The architecture review could not be parsed.",
                model,
                repair.Repaired,
                repair.Attempts,
                repaired);

        var repairedReview = ArchitectureReviewSemanticRepair.Repair(
            parsed.Value,
            out var semanticRepairAttempts);
        var combinedRepairAttempts = repair.Attempts
            .Concat(semanticRepairAttempts)
            .ToArray();
        var semanticallyRepaired = semanticRepairAttempts.Any(item =>
            item.Status == LlmRepairStatus.Succeeded);
        var errors = ArchitectureReviewValidator.Validate(
            plan,
            repairedReview);
        if (errors.Count > 0)
            return Failed(
                PlanningFailure.InvalidPlan,
                $"Architecture review validation failed: {string.Join(" ", errors)}",
                model,
                repair.Repaired || semanticallyRepaired,
                combinedRepairAttempts,
                repaired);

        logger.LogInformation(
            "Architecture review pass {Pass} completed with {FindingCount} finding(s); approved: {Approved}.",
            reviewPass,
            repairedReview.Findings.Count,
            repairedReview.Approved);
        return new(
            true,
            PlanningFailure.None,
            null,
            model,
            repairedReview,
            repair.Repaired || semanticallyRepaired,
            combinedRepairAttempts)
        {
            Usage = repaired.Usage,
            Diagnostics = repaired.Diagnostics,
            FinishReason = repaired.FinishReason
        };
    }

    private static ArchitectureReviewOutcome Failed(
        PlanningFailure failure,
        string error,
        string model,
        bool repaired = false,
        IReadOnlyList<LlmRepairAttempt>? attempts = null,
        Penghou.Baize.LlmResponse? response = null) =>
        new(false, failure, error, model, null, repaired,
            attempts ?? [])
        {
            Usage = response?.Usage,
            Diagnostics = response?.Diagnostics,
            FinishReason = response?.FinishReason
        };
}
