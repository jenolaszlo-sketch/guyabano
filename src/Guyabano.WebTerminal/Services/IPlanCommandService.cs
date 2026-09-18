namespace Guyabano.WebTerminal.Services;

/// <summary>Handles <c>/plan</c> (author-only) and <c>/run</c> (executes only admitted plans).</summary>
public interface IPlanCommandService
{
    Task<PlanCommandResult?> AuthorPlanAsync(string prompt, CancellationToken cancellationToken = default);

    Task<PlanExecutionResult> ExecutePlanAsync(CancellationToken cancellationToken = default);
}
