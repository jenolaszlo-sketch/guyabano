namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationWorkerOptions
{
    public const string SectionName = "CodeGeneration";

    public string Model { get; set; } = "deepseek-v4-flash";

    public List<string> EscalationModels { get; set; } = [];

    public string PlannerModel { get; set; } = "deepseek-v4-flash";

    public int PlannerMaxTokens { get; set; } = 24000;

    public string DecompositionModel { get; set; } =
        "deepseek-v4-flash";

    public List<string> DecompositionEscalationModels { get; set; } =
        [];

    public int DecompositionMaxTokens { get; set; } = 12000;

    public int DecompositionRetryMaxTokens { get; set; } = 24000;

    public string ArchitectureReviewModel { get; set; } =
        "deepseek-v4-flash";

    public int ArchitectureReviewMaxTokens { get; set; } = 16000;

    public string ArchitectureIntegratorModel { get; set; } =
        "deepseek-v4-flash";

    public int ArchitectureIntegratorMaxTokens { get; set; } = 20000;

    public string ProjectName { get; set; } = string.Empty;

    public string? RootNamespace { get; set; }

    public string TargetFramework { get; set; } = "net10.0";

    public string OutputRoot { get; set; } = string.Empty;

    public string CiRelativePath { get; set; } = ".";

    public bool RepositoryContextEnabled { get; set; } = true;

    public string RepositoryId { get; set; } = "repo:guyabano-generated";

    public List<string> RepositorySymbolSeeds { get; set; } = [];

    public bool IncludeRepositoryContextInPrompts { get; set; }

    public int RepositoryContextMaximumPromptCharacters { get; set; } = 40_000;

    public int DefaultMaxTokens { get; set; } = 8000;

    public int DefaultRetryMaxTokens { get; set; } = 16000;

    public Dictionary<string, CodeGenerationTokenBudgetOptions>
        ModelTokenBudgets
    { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
}
