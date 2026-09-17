namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Model and token configuration for Fuwen-hosted planning stages.
/// Binds to the existing <c>CodeGeneration</c> section, so the same
/// <c>appsettings.json</c> drives both the hard-coded workflow and the
/// Fuwen pilot (PlannerModel/PlannerMaxTokens plus per-stage ceilings
/// matching the staged service's budgets).
/// </summary>
public sealed class FuwenPlanningOptions
{
    public string PlannerModel { get; set; } = "deepseek-v4-flash";

    public int PlannerMaxTokens { get; set; } = 24000;

    public int DomainMaxTokens { get; set; } = 8000;

    public int TopologyMaxTokens { get; set; } = 10000;

    public int ContractMaxTokens { get; set; } = 12000;

    public int ComponentMaxTokens { get; set; } = 16000;

    public bool IncludeRepositoryContextInPrompts { get; set; } = false;

    public int RepositoryContextMaximumPromptCharacters { get; set; } = 40000;
}
