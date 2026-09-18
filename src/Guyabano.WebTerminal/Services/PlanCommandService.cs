using Guyabano.CodeGeneration.Planning;
using Microsoft.Extensions.Options;
using Penghou.Fuwen.Compiler;
using Guyabano.WorkflowWorker;

namespace Guyabano.WebTerminal.Services;

/// <summary>Authored plan plus its admission verdict for UI display.</summary>
public sealed record PlanCommandResult(
    string Dsl,
    bool Admitted,
    IReadOnlyList<string> Diagnostics,
    int Attempts);

/// <summary>
/// Handles <c>/plan</c> chat commands: authors a Fuwen workflow from the
/// request through <see cref="WorkflowAuthor"/> and returns the DSL plus
/// its admission verdict for display. Never executes anything.
/// </summary>
public sealed class PlanCommandService(
    WorkflowAuthor author,
    PlanCommandCatalogue catalogueSource,
    IOptions<CodeGenerationWorkerOptions> options) : IPlanCommandService
{
    public const string CommandPrefix = "/plan";

    public static bool IsPlanCommand(string prompt) =>
        prompt.TrimStart().StartsWith(CommandPrefix, StringComparison.OrdinalIgnoreCase);

    public static string StripPrefix(string prompt)
    {
        var trimmed = prompt.TrimStart();
        var rest = trimmed.StartsWith(CommandPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[CommandPrefix.Length..]
            : trimmed;
        return rest.TrimStart();
    }

    public async Task<PlanCommandResult?> AuthorPlanAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (!IsPlanCommand(prompt))
            return null;
        var request = StripPrefix(prompt);
        if (string.IsNullOrWhiteSpace(request))
            return new PlanCommandResult(string.Empty, false, ["Usage: /plan <request>"], 0);
        var settings = options.Value;
        var result = await author.AuthorAsync(
            request,
            catalogueSource.Summary,
            catalogueSource.Catalogue,
            settings.PlannerModel,
            settings.PlannerMaxTokens,
            cancellationToken).ConfigureAwait(false);
        return new PlanCommandResult(
            result.Dsl,
            result.Succeeded,
            result.Succeeded
                ? []
                : result.Diagnostics.Count > 0
                    ? result.Diagnostics
                    : ["The model did not produce an admittable workflow within the attempt budget."],
            result.Attempts.Count);
    }
}
