namespace Guyabano.WebTerminal.Services;

/// <summary>Handles <c>/plan</c> chat commands (author-only, never executes).</summary>
public interface IPlanCommandService
{
    Task<PlanCommandResult?> AuthorPlanAsync(string prompt, CancellationToken cancellationToken = default);
}
