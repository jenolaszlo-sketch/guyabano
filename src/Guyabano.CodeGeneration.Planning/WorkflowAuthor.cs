using Penghou.Baize;
using Penghou.Baize.Router;
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
    public async Task<WorkflowAuthorResult> AuthorAsync(
        string request,
        string catalogueSummary,
        ITrustedCatalogue catalogue,
        string model,
        int maxTokens = 4000,
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
                new WorkflowAuthoringPromptContext(request, catalogueSummary, maxTokens, previousFailure),
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
