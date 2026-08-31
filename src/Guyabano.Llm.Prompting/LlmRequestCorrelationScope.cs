namespace Guyabano.Llm.Prompting;

public sealed record LlmRequestCorrelation(
    string SessionId,
    string WorkflowRunId,
    string WorkflowStepKey,
    Guid? CangjieSnapshotId = null,
    string? CangjieStrategy = null,
    string? CangjieStrategyVersion = null,
    string? CangjieQueryIdentity = null,
    string? CangjiePurpose = null,
    string? HetuIndexRunId = null,
    string? HetuIndexIdentity = null,
    string? WorkspaceRevision = null,
    int? WorkflowStepRevision = null);

public static class LlmRequestCorrelationScope
{
    private static readonly AsyncLocal<CorrelationState?> CurrentValue =
        new();

    public static LlmRequestCorrelation? Current =>
        CurrentValue.Value?.Correlation;

    public static int NextInvocationOrdinal()
    {
        var state = CurrentValue.Value ?? throw new InvalidOperationException(
            "An LLM request correlation scope is required.");
        return Interlocked.Increment(ref state.InvocationOrdinal);
    }

    public static IDisposable Push(LlmRequestCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlation.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlation.WorkflowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlation.WorkflowStepKey);

        var previous = CurrentValue.Value;
        CurrentValue.Value = new CorrelationState(correlation);
        return new Scope(previous);
    }

    private sealed class Scope(CorrelationState? previous) : IDisposable
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

    private sealed class CorrelationState(LlmRequestCorrelation correlation)
    {
        public LlmRequestCorrelation Correlation { get; } = correlation;

        public int InvocationOrdinal;
    }
}
