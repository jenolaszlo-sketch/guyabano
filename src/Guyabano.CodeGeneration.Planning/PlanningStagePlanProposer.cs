using System.Text.Json;
using Penghou.Baize.Router;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>One stage-plan proposal attempt: the model text plus its validation result.</summary>
public sealed record PlanningStagePlanProposalAttempt(
    int Attempt,
    string Content,
    bool Accepted,
    IReadOnlyList<string> Diagnostics);

/// <summary>Bounded proposal outcome: an admitted stage plan or exhausted diagnostics.</summary>
public sealed record PlanningStagePlanProposalResult(
    bool Succeeded,
    PlanningStagePlan? Plan,
    IReadOnlyList<PlanningStagePlanProposalAttempt> Attempts,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Proposes which planning artifacts an objective requires: renders the
/// bootstrap pack, calls the model, parses the stage-plan JSON, stamps
/// provenance deterministically (producer identity, observed revisions,
/// catalogue version), and validates admission with repair feedback until
/// the plan admits or attempts run out. The model proposes; validation
/// owns admission.
/// </summary>
public sealed class PlanningStagePlanProposer(
    ILlmRouter llmRouter,
    IPromptBuilder<PlanningBootstrapPromptContext> promptBuilder,
    int maxAttempts = 3,
    int maxFailureCharacters = 4000)
{
    public async Task<PlanningStagePlanProposalResult> ProposeAsync(
        string goal,
        PlanningStageCatalogue catalogue,
        IReadOnlyList<string> currentRevisions,
        string model,
        int maxTokens = 4000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(currentRevisions);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        var known = currentRevisions
            .Where(revision => !string.IsNullOrWhiteSpace(revision))
            .ToHashSet(StringComparer.Ordinal);
        var attempts = new List<PlanningStagePlanProposalAttempt>();
        string? previousFailure = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var llmRequest = await promptBuilder.BuildAsync(
                new PlanningBootstrapPromptContext(
                    goal,
                    PlanningStageCatalogueSummary.Render(catalogue),
                    catalogue.Version,
                    known.OrderBy(revision => revision, StringComparer.Ordinal).ToArray(),
                    maxTokens,
                    previousFailure),
                cancellationToken).ConfigureAwait(false);
            var response = await llmRouter.CompleteStreamingAsync(
                model, llmRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                previousFailure = Truncate("The stage planner returned no response.");
                attempts.Add(new PlanningStagePlanProposalAttempt(
                    attempt, string.Empty, false, [previousFailure]));
                continue;
            }

            var content = WorkflowAuthor.ExtractDsl(response.Content ?? string.Empty);
            var draft = TryParse(content, out var parseError);
            if (draft is null)
            {
                previousFailure = Truncate(parseError!);
                attempts.Add(new PlanningStagePlanProposalAttempt(
                    attempt, content, false, [previousFailure]));
                continue;
            }

            var plan = draft with
            {
                Provenance = new PlanningStagePlanProvenance
                {
                    ProducedBy = $"llm:{model}",
                    InputRevisions = known
                        .OrderBy(revision => revision, StringComparer.Ordinal).ToArray(),
                    DefinitionCatalogueVersion = catalogue.Version,
                },
            };
            var errors = PlanningStageValidator.Validate(plan, catalogue, known);
            if (errors.Count > 0)
            {
                previousFailure = Truncate(string.Join(" ", errors));
                attempts.Add(new PlanningStagePlanProposalAttempt(
                    attempt, content, false, [previousFailure]));
                continue;
            }

            attempts.Add(new PlanningStagePlanProposalAttempt(attempt, content, true, []));
            return new PlanningStagePlanProposalResult(true, plan, attempts, []);
        }

        var last = attempts[^1];
        return new PlanningStagePlanProposalResult(
            false, null, attempts,
            attempts.SelectMany(attempt => attempt.Diagnostics).ToArray());
    }

    private static PlanningStagePlan? TryParse(string content, out string? error)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<PlanningStagePlan>(content);
            if (plan is null)
            {
                error = "The proposal is JSON null, not a stage plan.";
                return null;
            }

            error = null;
            return plan;
        }
        catch (JsonException exception)
        {
            error = $"The proposal is not a valid stage plan: {exception.Message}";
            return null;
        }
    }

    private string Truncate(string value) =>
        value.Length <= maxFailureCharacters
            ? value
            : value[..maxFailureCharacters] + "…[truncated]";
}
