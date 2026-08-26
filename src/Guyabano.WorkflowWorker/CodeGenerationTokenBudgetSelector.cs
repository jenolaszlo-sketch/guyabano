namespace Guyabano.WorkflowWorker;

internal static class CodeGenerationTokenBudgetSelector
{
    public static int Select(
        CodeGenerationWorkerOptions options,
        string model,
        int attempt)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var configured = options.ModelTokenBudgets.TryGetValue(
            model,
            out var modelBudget)
            ? modelBudget
            : null;
        var tokens = attempt > 1
            ? configured?.RetryMaxTokens ??
                options.DefaultRetryMaxTokens
            : configured?.MaxTokens ??
                options.DefaultMaxTokens;

        if (tokens <= 0)
        {
            throw new InvalidOperationException(
                $"The configured token budget for model '{model}' must be positive.");
        }

        return tokens;
    }
}
