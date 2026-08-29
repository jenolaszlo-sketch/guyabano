using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Guyabano.Session;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Creates one embedded Zhinu runtime and workflow database per session while
/// keeping the process-wide handle cache bounded.
/// </summary>
public sealed class SessionWorkflowRuntimeProvider :
    ISessionWorkflowRuntimeProvider,
    IAsyncDisposable
{
    private readonly string sessionsRoot;
    private readonly IWorkflowRegistry registry;
    private readonly IWorkflowStepResolver stepResolver;
    private readonly ZhinuOptions zhinuOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly IWorkflowEventPublisher? eventPublisher;
    private readonly int maximumCachedRuntimes;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<GuyabanoSessionId, Entry> entries = [];
    private bool disposed;

    public SessionWorkflowRuntimeProvider(
        string sessionsRoot,
        IWorkflowRegistry registry,
        IWorkflowStepResolver stepResolver,
        ZhinuOptions zhinuOptions,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IWorkflowEventPublisher? eventPublisher = null,
        int maximumCachedRuntimes = 16)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsRoot);
        if (maximumCachedRuntimes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCachedRuntimes));
        this.sessionsRoot = Path.GetFullPath(sessionsRoot);
        this.registry = registry;
        this.stepResolver = stepResolver;
        this.zhinuOptions = zhinuOptions;
        this.timeProvider = timeProvider;
        this.loggerFactory = loggerFactory;
        this.eventPublisher = eventPublisher;
        this.maximumCachedRuntimes = maximumCachedRuntimes;
    }

    public async ValueTask<ISessionWorkflowRuntimeLease> AcquireAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        List<WorkflowEngine>? evicted = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(sessionId, out var entry))
            {
                var databasePath = Path.Combine(
                    sessionsRoot,
                    sessionId.ToString(),
                    "workflow.db");
                var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
                {
                    DatabasePath = databasePath,
                    TimeProvider = timeProvider
                });
                var builder = new WorkflowEngineBuilder()
                    .WithStore(store)
                    .WithRegistry(registry)
                    .WithOptions(zhinuOptions)
                    .WithTimeProvider(timeProvider)
                    .WithLogger(loggerFactory.CreateLogger<WorkflowEngine>())
                    .WithStepResolver(stepResolver);
                if (eventPublisher is not null)
                    builder.WithEventPublisher(eventPublisher);
                var engine = builder.Build();
                entry = new Entry(engine);
                entries.Add(sessionId, entry);
            }

            entry.ReferenceCount++;
            entry.LastUsed = timeProvider.GetUtcNow();
            evicted = TrimUnlocked(sessionId);
            return new RuntimeLease(this, sessionId, entry.Engine);
        }
        finally
        {
            gate.Release();
            if (evicted is not null)
                foreach (var engine in evicted)
                    await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        WorkflowEngine[] engines;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;
            engines = entries.Values.Select(item => item.Engine).ToArray();
            entries.Clear();
        }
        finally
        {
            gate.Release();
        }
        foreach (var engine in engines)
            await engine.DisposeAsync().ConfigureAwait(false);
        gate.Dispose();
    }

    private async ValueTask ReleaseAsync(GuyabanoSessionId sessionId)
    {
        List<WorkflowEngine>? evicted;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!entries.TryGetValue(sessionId, out var entry))
                return;
            entry.ReferenceCount--;
            entry.LastUsed = timeProvider.GetUtcNow();
            evicted = TrimUnlocked(default);
        }
        finally
        {
            gate.Release();
        }
        if (evicted is not null)
            foreach (var engine in evicted)
                await engine.DisposeAsync().ConfigureAwait(false);
    }

    private List<WorkflowEngine>? TrimUnlocked(GuyabanoSessionId protectedSession)
    {
        List<WorkflowEngine>? evicted = null;
        while (entries.Count > maximumCachedRuntimes)
        {
            var candidate = entries
                .Where(pair => pair.Key != protectedSession && pair.Value.ReferenceCount == 0)
                .OrderBy(pair => pair.Value.LastUsed)
                .FirstOrDefault();
            if (candidate.Value is null)
                break;
            entries.Remove(candidate.Key);
            (evicted ??= []).Add(candidate.Value.Engine);
        }
        return evicted;
    }

    private sealed class Entry(WorkflowEngine engine)
    {
        public WorkflowEngine Engine { get; } = engine;
        public int ReferenceCount { get; set; }
        public DateTimeOffset LastUsed { get; set; }
    }

    private sealed class RuntimeLease(
        SessionWorkflowRuntimeProvider owner,
        GuyabanoSessionId sessionId,
        WorkflowEngine engine) : ISessionWorkflowRuntimeLease
    {
        private int disposed;
        public GuyabanoSessionId SessionId { get; } = sessionId;
        public WorkflowEngine Engine { get; } = engine;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref disposed, 1) == 0
                ? owner.ReleaseAsync(SessionId)
                : ValueTask.CompletedTask;
    }
}

/// <summary>DI-backed class step activation used by every session runtime.</summary>
public sealed class GuyabanoWorkflowStepResolver(IServiceScopeFactory scopeFactory) :
    IWorkflowStepResolver
{
    public async ValueTask<IWorkflowStepLease<TStep>> ResolveAsync<TStep>(
        StepImplementationKey implementationKey,
        CancellationToken cancellationToken)
        where TStep : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var matches = scope.ServiceProvider
                .GetKeyedServices<TStep>(implementationKey)
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
                throw new WorkflowConfigurationException(
                    matches.Length == 0
                        ? $"Could not resolve workflow step '{implementationKey}' as '{typeof(TStep).FullName}'."
                        : $"Multiple workflow steps are registered for '{implementationKey}' as '{typeof(TStep).FullName}'.");
            return new StepLease<TStep>(scope, matches[0]);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class StepLease<TStep>(AsyncServiceScope scope, TStep step) :
        IWorkflowStepLease<TStep>
        where TStep : class
    {
        public TStep Step { get; } = step;
        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
