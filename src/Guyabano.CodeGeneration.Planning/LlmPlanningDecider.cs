using System.Text.Json;
using Penghou.Baize.Router;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Model-backed decider: renders the planning-decision pack from the live
/// observation, parses the proposed JSON, and repairs parse failures until
/// the decision parses or attempts run out. Semantic admission (basis,
/// motivation, budget) belongs to the driver, not here.
/// </summary>
public sealed class LlmPlanningDecider(
    ILlmRouter llmRouter,
    IPromptBuilder<PlanningDecisionPromptContext> promptBuilder,
    string model,
    int maxTokens = 2000,
    int maxAttempts = 3,
    int maxFailureCharacters = 4000) : IPlanningDecider
{
    public async Task<PlanningDecisionResult> DecideAsync(
        PlanningLoopObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        string? previousFailure = null;
        var calls = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var llmRequest = await promptBuilder.BuildAsync(
                new PlanningDecisionPromptContext(
                    observation.Goal,
                    observation.ExecutionPlan,
                    observation.DesignFingerprint,
                    observation.WorkflowVersion,
                    observation.WorkflowComplete,
                    observation.EvidenceSummary,
                    observation.FreshArtifactRevisions,
                    observation.Remaining.IterationsRemaining,
                    observation.Remaining.MutationsRemaining,
                    observation.Remaining.StructuralIterationsRemaining,
                    observation.Remaining.ModelCallsRemaining,
                    observation.Remaining.TokensRemaining,
                    maxTokens,
                    previousFailure),
                cancellationToken).ConfigureAwait(false);
            calls++;
            var response = await llmRouter.CompleteStreamingAsync(
                model, llmRequest, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (response is null)
            {
                previousFailure = Truncate("The planning decider returned no response.");
                continue;
            }

            var content = WorkflowAuthor.ExtractDsl(response.Content ?? string.Empty);
            var decision = TryParse(content, out var error);
            if (decision is not null)
            {
                return new PlanningDecisionResult(true, decision, calls, []);
            }

            previousFailure = Truncate(error!);
        }

        return new PlanningDecisionResult(false, null, calls, [previousFailure ?? "No decision."]);
    }

    private static PlanningDecision? TryParse(string content, out string? error)
    {
        try
        {
            var decision = JsonSerializer.Deserialize<PlanningDecision>(content);
            if (decision is null)
            {
                error = "The decision is JSON null, not a planning decision.";
                return null;
            }

            error = null;
            return decision;
        }
        catch (JsonException exception)
        {
            error = $"The decision is not a valid planning decision: {exception.Message}";
            return null;
        }
    }

    private string Truncate(string value) =>
        value.Length <= maxFailureCharacters
            ? value
            : value[..maxFailureCharacters] + "…[truncated]";
}
