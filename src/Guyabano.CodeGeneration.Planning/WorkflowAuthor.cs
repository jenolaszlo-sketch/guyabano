using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>One authoring attempt: the model text plus its compile result.</summary>
public sealed record WorkflowAuthorAttempt(
    int Attempt,
    string Dsl,
    bool Admitted,
    IReadOnlyList<string> Diagnostics);

/// <summary>Bounded author outcome: admitted plan or exhausted diagnostics.</summary>
public sealed record WorkflowAuthorResult(
    bool Succeeded,
    string Dsl,
    WorkflowAdmissionResult? Admission,
    IReadOnlyList<WorkflowAuthorAttempt> Attempts,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Authors an executable workflow from a user request: renders the
/// workflow-authoring pack, calls the model, and repairs compiler
/// diagnostics back through <c>previousFailure</c> until the plan admits
/// or the attempt budget is exhausted. Only admitted plans proceed; raw
/// model text is never executed.
/// </summary>
public sealed class WorkflowAuthor(
    ILlmRouter llmRouter,
    IPromptBuilder<WorkflowAuthoringPromptContext> promptBuilder,
    int maxAttempts = 3,
    int maxFailureCharacters = 4000)
{
    public Task<WorkflowAuthorResult> AuthorAsync(
        string request,
        string catalogueSummary,
        ITrustedCatalogue catalogue,
        string model,
        int maxTokens = 4000,
        CancellationToken cancellationToken = default) =>
        AuthorCoreAsync(
            request,
            executionPlan: null,
            catalogueSummary,
            catalogue,
            model,
            maxTokens,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Authors an executable workflow by translating a resolved execution
    /// design: the plan section tells the model exactly which steps, edges,
    /// and descriptors to emit, and the same compile–repair loop admits the
    /// result. The goal states what the plan decomposes; the plan itself is
    /// authoritative.
    /// </summary>
    public Task<WorkflowAuthorResult> AuthorFromPlanAsync(
        string goal,
        string executionPlan,
        string catalogueSummary,
        ITrustedCatalogue catalogue,
        string model,
        int maxTokens = 4000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionPlan);
        return AuthorCoreAsync(
            goal,
            executionPlan,
            catalogueSummary,
            catalogue,
            model,
            maxTokens,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Authors the next workflow revision from a merged execution design. The
    /// model receives the prior admitted DSL with the patch's changed steps
    /// and must reproduce every other node verbatim; after admission the
    /// preservation check verifies that contract, and drift is fed back as
    /// repair feedback until the candidate is clean or attempts run out.
    /// </summary>
    public Task<WorkflowAuthorResult> AuthorFromPatchAsync(
        string goal,
        string executionPlan,
        string priorDsl,
        WorkflowPlan priorPlan,
        WorkflowPatch patch,
        string catalogueSummary,
        ITrustedCatalogue catalogue,
        string model,
        int maxTokens = 4000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(priorDsl);
        ArgumentNullException.ThrowIfNull(priorPlan);
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.AffectedStepIds().Count == 0)
        {
            throw new ArgumentException("Patch affects no steps.", nameof(patch));
        }

        return AuthorCoreAsync(
            goal,
            executionPlan,
            catalogueSummary,
            catalogue,
            model,
            maxTokens,
            patch,
            priorDsl,
            priorPlan,
            cancellationToken);
    }

    private async Task<WorkflowAuthorResult> AuthorCoreAsync(
        string request,
        string? executionPlan,
        string catalogueSummary,
        ITrustedCatalogue catalogue,
        string model,
        int maxTokens = 4000,
        WorkflowPatch? patch = null,
        string? priorDsl = null,
        WorkflowPlan? priorPlan = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueSummary);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        var attempts = new List<WorkflowAuthorAttempt>();
        string? previousFailure = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var llmRequest = await promptBuilder.BuildAsync(
                new WorkflowAuthoringPromptContext(
                    request,
                    catalogueSummary,
                    maxTokens,
                    previousFailure,
                    executionPlan,
                    PriorDsl: patch is null ? null : priorDsl,
                    ChangedSteps: patch?.AffectedStepIds()
                        .OrderBy(id => id, StringComparer.Ordinal).ToArray()),
                cancellationToken).ConfigureAwait(false);
            var response = await llmRouter.CompleteStreamingAsync(
                model, llmRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                previousFailure = Truncate("The authoring model returned no response.");
                attempts.Add(new WorkflowAuthorAttempt(attempt, string.Empty, false, [previousFailure]));
                continue;
            }
            var dsl = ExtractDsl(response.Content ?? string.Empty);
            var compiler = new FuwenSourceCompiler(catalogue);
            var compiled = await compiler.CompileAsync(dsl, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!compiled.Succeeded || compiled.Plan is null)
            {
                previousFailure = Truncate(string.Join("; ", compiled.Diagnostics
                    .Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
                attempts.Add(new WorkflowAuthorAttempt(attempt, dsl, false, [previousFailure]));
                continue;
            }
            var admission = await new WorkflowAdmissionService(
                    new WorkflowCompiler(catalogue,
                        capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
                .AdmitAsync(compiled.Plan, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!admission.Succeeded)
            {
                previousFailure = Truncate(string.Join("; ", admission.Diagnostics
                    .Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
                attempts.Add(new WorkflowAuthorAttempt(attempt, dsl, false, [previousFailure]));
                continue;
            }

            if (patch is not null && priorPlan is not null)
            {
                var definition = admission.Compilation.Definition;
                if (definition is null)
                {
                    previousFailure = Truncate("The admitted plan has no readable definition.");
                    attempts.Add(new WorkflowAuthorAttempt(attempt, dsl, true, [previousFailure]));
                    continue;
                }

                var drifts = PatchPreservationValidator.Validate(
                    priorPlan, definition.ReadPlan(), patch);
                if (drifts.Count > 0)
                {
                    previousFailure = Truncate(
                        "Preservation check failed: " + string.Join("; ", drifts));
                    attempts.Add(new WorkflowAuthorAttempt(attempt, dsl, true, [previousFailure]));
                    continue;
                }
            }

            attempts.Add(new WorkflowAuthorAttempt(attempt, dsl, true, []));
            return new WorkflowAuthorResult(true, dsl, admission, attempts, []);
        }

        var last = attempts[^1];
        return new WorkflowAuthorResult(false, last.Dsl, null, attempts, last.Diagnostics);
    }

    private string Truncate(string value) =>
        value.Length <= maxFailureCharacters
            ? value
            : value[..maxFailureCharacters] + "…[truncated]";

    /// <summary>
    /// Extracts workflow source from model text, unwrapping an optional
    /// Markdown fence. The compiler remains the authority; this is only
    /// hygiene before compilation.
    /// </summary>
    public static string ExtractDsl(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = content.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;
        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline < 0 || lastFence <= firstNewline)
            return text;
        return text[(firstNewline + 1)..lastFence].Trim();
    }
}
