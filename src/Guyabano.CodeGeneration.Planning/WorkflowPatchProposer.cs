using System.Text.Json;
using Penghou.Baize.Router;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>One patch proposal attempt: the model text plus its validation result.</summary>
public sealed record WorkflowPatchProposalAttempt(
    int Attempt,
    string Content,
    bool Accepted,
    IReadOnlyList<string> Diagnostics);

/// <summary>Bounded proposal outcome: an applied patch or exhausted diagnostics.</summary>
public sealed record WorkflowPatchProposalResult(
    bool Succeeded,
    WorkflowPatch? Patch,
    PlannedExecutionDesign? Applied,
    IReadOnlyList<WorkflowPatchProposalAttempt> Attempts,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Proposes a <see cref="WorkflowPatch"/> against a current execution design:
/// renders the workflow-patch pack, calls the model, parses the JSON, and
/// runs deterministic validation (provenance subset plus
/// <see cref="StagedExecutionGraphBuilder.Apply"/>) with repair feedback
/// until the patch applies or the attempt budget is exhausted. The model only
/// proposes text; validation owns admission.
/// </summary>
public sealed class WorkflowPatchProposer(
    ILlmRouter llmRouter,
    IPromptBuilder<WorkflowPatchPromptContext> promptBuilder,
    int maxAttempts = 3,
    int maxFailureCharacters = 4000)
{
    public async Task<WorkflowPatchProposalResult> ProposeAsync(
        string goal,
        PlannedExecutionDesign current,
        string executionPlan,
        IReadOnlyList<string> changedArtifacts,
        string catalogueSummary,
        string model,
        int maxTokens = 4000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionPlan);
        ArgumentNullException.ThrowIfNull(changedArtifacts);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        var allowed = changedArtifacts
            .Where(artifact => !string.IsNullOrWhiteSpace(artifact))
            .ToArray();
        if (allowed.Length == 0)
        {
            throw new ArgumentException(
                "Changed artifacts must not be empty.",
                nameof(changedArtifacts));
        }

        var baseFingerprint = StagedExecutionDesignIdentity.Compute(current);
        var attempts = new List<WorkflowPatchProposalAttempt>();
        string? previousFailure = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var llmRequest = await promptBuilder.BuildAsync(
                new WorkflowPatchPromptContext(
                    goal,
                    executionPlan,
                    baseFingerprint,
                    allowed,
                    catalogueSummary,
                    maxTokens,
                    previousFailure),
                cancellationToken).ConfigureAwait(false);
            var response = await llmRouter.CompleteStreamingAsync(
                model, llmRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                previousFailure = Truncate("The patch proposer returned no response.");
                attempts.Add(new WorkflowPatchProposalAttempt(
                    attempt, string.Empty, false, [previousFailure]));
                continue;
            }

            var content = WorkflowAuthor.ExtractDsl(response.Content ?? string.Empty);
            var patch = TryParse(content, out var parseError);
            if (patch is null)
            {
                previousFailure = Truncate(parseError!);
                attempts.Add(new WorkflowPatchProposalAttempt(
                    attempt, content, false, [previousFailure]));
                continue;
            }

            var applied = ValidatePatch(current, patch, allowed, out var validationError);
            if (applied is null)
            {
                previousFailure = Truncate(validationError!);
                attempts.Add(new WorkflowPatchProposalAttempt(
                    attempt, content, false, [previousFailure]));
                continue;
            }

            attempts.Add(new WorkflowPatchProposalAttempt(attempt, content, true, []));
            return new WorkflowPatchProposalResult(true, patch, applied, attempts, []);
        }

        var last = attempts[^1];
        return new WorkflowPatchProposalResult(false, null, null, attempts, last.Diagnostics);
    }

    private static WorkflowPatch? TryParse(string content, out string? error)
    {
        try
        {
            var patch = JsonSerializer.Deserialize<WorkflowPatch>(content);
            if (patch is null)
            {
                error = "The proposal is JSON null, not a workflow patch.";
                return null;
            }

            error = null;
            return patch;
        }
        catch (JsonException exception)
        {
            error = $"The proposal is not a valid workflow patch: {exception.Message}";
            return null;
        }
    }

    private static PlannedExecutionDesign? ValidatePatch(
        PlannedExecutionDesign current,
        WorkflowPatch patch,
        IReadOnlyList<string> allowed,
        out string? error)
    {
        var outside = patch.DerivedFromArtifacts
            .Where(artifact => !allowed.Contains(artifact, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(artifact => artifact, StringComparer.Ordinal)
            .ToArray();
        if (outside.Length > 0)
        {
            error = "The proposal derives from artifacts outside the supplied change set: " +
                string.Join(", ", outside) + ".";
            return null;
        }

        try
        {
            var applied = StagedExecutionGraphBuilder.Apply(current, patch);
            error = null;
            return applied;
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private string Truncate(string value) =>
        value.Length <= maxFailureCharacters
            ? value
            : value[..maxFailureCharacters] + "…[truncated]";
}
