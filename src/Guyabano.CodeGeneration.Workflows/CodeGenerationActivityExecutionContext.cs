using System.Threading;

namespace Guyabano.CodeGeneration.Workflows;

public sealed class CodeGenerationActivityExecutionContext
{
    private static readonly AsyncLocal<CodeGenerationActivityExecutionContext?>
        CurrentContext = new();

    public CodeGenerationActivityExecutionContext(
        string workflowId,
        string workflowRunId,
        string activityId,
        int attempt,
        CancellationToken cancellationToken,
        CodeGenerationActivityHeartbeatState heartbeatState)
    {
        Info = new CodeGenerationActivityInfo(
            workflowId,
            workflowRunId,
            activityId,
            attempt,
            heartbeatState);
        CancellationToken = cancellationToken;
    }

    public static CodeGenerationActivityExecutionContext Current =>
        CurrentContext.Value ??
        throw new InvalidOperationException(
            "No code-generation activity is currently executing.");

    public CodeGenerationActivityInfo Info { get; }

    public CancellationToken CancellationToken { get; }

    public void Heartbeat<T>(T detail) => Info.SetHeartbeat(detail);

    public static IDisposable Push(
        CodeGenerationActivityExecutionContext context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(
        CodeGenerationActivityExecutionContext? previous) : IDisposable
    {
        public void Dispose() => CurrentContext.Value = previous;
    }
}

public sealed class CodeGenerationActivityInfo(
    string workflowId,
    string workflowRunId,
    string activityId,
    int attempt,
    CodeGenerationActivityHeartbeatState heartbeatState)
{
    public string WorkflowId { get; } = workflowId;

    public string WorkflowRunId { get; } = workflowRunId;

    public string ActivityId { get; } = activityId;

    public int Attempt { get; } = attempt;

    public IReadOnlyList<object?> HeartbeatDetails =>
        heartbeatState.Detail is null ? [] : [heartbeatState.Detail];

    public Task<T> HeartbeatDetailAtAsync<T>(int index)
    {
        if (index != 0 || heartbeatState.Detail is not T detail)
            throw new ArgumentOutOfRangeException(nameof(index));
        return Task.FromResult(detail);
    }

    internal void SetHeartbeat<T>(T detail) =>
        heartbeatState.Detail = detail;
}

public sealed class CodeGenerationActivityHeartbeatState
{
    public object? Detail { get; set; }
}
