using Microsoft.Extensions.Logging;
using Penghou.Baize;
using Penghou.Baize.Router;
using Guyabano.Llm.Prompting;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;

namespace Guyabano.CodeGeneration.Planning;

internal sealed class ArchitectureDecisionIntegrator(
    ILlmRouter router,
    IPromptBuilder<ArchitectureDecisionIntegrationPromptContext> promptBuilder,
    ILlmResponseNormalizer normalizer,
    ArchitectureDecisionPatchParser parser,
    ILogger<ArchitectureDecisionIntegrator> logger)
    : IArchitectureDecisionIntegrator
{
    internal const string ToolName = "return_architecture_decision_patch";

    public async Task<ArchitectureDecisionIntegrationOutcome> IntegrateAsync(
        CodeGenerationPlan plan,
        ArchitectureReview resolvedReview,
        IReadOnlyList<ArchitectureGapResolution> resolvedDecisions,
        string model,
        int maxTokens,
        string? previousFailure = null,
        CancellationToken cancellationToken = default)
    {
        var tool = new LlmTool(
            ToolName,
            "Returns a constrained architecture patch resolving every review finding.",
            JsonSchemaGenerator.GenerateSchemaJson<ArchitectureDecisionPatch>());
        var request = await promptBuilder.BuildAsync(
            new(
                plan,
                resolvedReview,
                resolvedDecisions,
                previousFailure,
                tool.Name,
                [tool],
                maxTokens),
            cancellationToken);
        var response = await router.CompleteStreamingAsync(
            model,
            request,
            cancellationToken: cancellationToken);
        if (response is null)
            return Failed(
                PlanningFailure.NoResponse,
                "The architecture decision integrator returned no response.",
                model);

        var normalized = await normalizer.NormalizeAsync(
            response,
            request.Tools.ToArray(),
            cancellationToken);
        var repair = ArchitecturePlanningFailureMapper.GetRepairInfo(
            normalized,
            ToolName);
        var parsed = parser.Parse(normalized);
        if (!parsed.Succeeded || parsed.Value is null)
            return Failed(
                ArchitecturePlanningFailureMapper.Map(parsed.Failure),
                parsed.Error ?? "The architecture decision patch could not be parsed.",
                model,
                repair.Repaired,
                repair.Attempts,
                response);

        try
        {
            var integrated = ArchitectureDecisionPatchApplier.Apply(
                plan,
                resolvedReview,
                resolvedDecisions,
                parsed.Value);
            logger.LogInformation(
                "Architecture decision integration applied {FindingCount} resolved finding(s) using {Model}.",
                resolvedReview.Findings.Count,
                model);
            return new(
                true,
                PlanningFailure.None,
                null,
                model,
                parsed.Value,
                integrated,
                repair.Repaired,
                repair.Attempts)
            {
                Usage = response.Usage,
                Diagnostics = response.Diagnostics,
                FinishReason = response.FinishReason
            };
        }
        catch (InvalidOperationException exception)
        {
            return Failed(
                PlanningFailure.InvalidPlan,
                exception.Message,
                model,
                repair.Repaired,
                repair.Attempts,
                response);
        }
    }

    private static ArchitectureDecisionIntegrationOutcome Failed(
        PlanningFailure failure,
        string error,
        string model,
        bool repaired = false,
        IReadOnlyList<LlmRepairAttempt>? attempts = null,
        Penghou.Baize.LlmResponse? response = null) =>
        new(false, failure, error, model, null, null, repaired,
            attempts ?? [])
        {
            Usage = response?.Usage,
            Diagnostics = response?.Diagnostics,
            FinishReason = response?.FinishReason
        };
}
