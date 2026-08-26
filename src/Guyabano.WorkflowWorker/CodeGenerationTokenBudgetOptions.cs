namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationTokenBudgetOptions
{
    public int MaxTokens { get; set; } = 8000;

    public int RetryMaxTokens { get; set; } = 16000;
}
