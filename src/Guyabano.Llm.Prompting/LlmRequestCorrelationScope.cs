namespace Guyabano.Llm.Prompting;

public sealed record LlmRequestCorrelation(
    string SessionId,
    string WorkflowRunId,
    string WorkflowStepKey);

public static class LlmRequestCorrelationScope
{
    private static readonly AsyncLocal<LlmRequestCorrelation?> CurrentValue =
        new();

    public static LlmRequestCorrelation? Current => CurrentValue.Value;

    public static IDisposable Push(LlmRequestCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlation.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlation.WorkflowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlation.WorkflowStepKey);

        var previous = CurrentValue.Value;
        CurrentValue.Value = correlation;
        return new Scope(previous);
    }

    private sealed class Scope(LlmRequestCorrelation? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;

            CurrentValue.Value = previous;
            disposed = true;
        }
    }
}
