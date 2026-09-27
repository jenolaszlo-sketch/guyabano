using System.Text.Json;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Shared engine lifecycle for parity scenarios: admit a plan, register it,
/// and start a run on an isolated SQLite store. Test files supply their own
/// plans, catalogues, and scripted providers; disposal stops the engine and
/// removes the temp directory with retries for locked files.
/// </summary>
internal sealed class ParityRun : IAsyncDisposable
{
    public WorkflowEngine Engine { get; }
    public Guid RunId { get; }

    private readonly string _root;

    private ParityRun(string root, WorkflowEngine engine, Guid runId)
    {
        _root = root;
        Engine = engine;
        RunId = runId;
    }

    public static async Task<ParityRun> StartAsync(
        WorkflowPlan plan,
        ITrustedCatalogue catalogue,
        FuwenZhinuExecutionPorts ports,
        string workflowName,
        string inputJson,
        CancellationToken ct)
    {
        var admission = await new WorkflowAdmissionService(
                new WorkflowCompiler(catalogue,
                    capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Succeeded)
            throw new InvalidOperationException(
                $"Admission failed: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path} expected={d.Expected} actual={d.Actual}"))}");
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                ports)
            .CreateAsync(workflowName, "1", admission, ct)
            .ConfigureAwait(false);
        var root = Path.Combine(
            Path.GetTempPath(), "guyabano-parity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "workflow.db"),
            Pooling = false,
        });
        var engine = new WorkflowEngine(store,
            registration.Register(new WorkflowRegistry()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
        using var input = JsonDocument.Parse(inputJson);
        var runId = await engine.StartAsync(workflowName, "1", input.RootElement.Clone(),
            cancellationToken: ct).ConfigureAwait(false);
        return new ParityRun(root, engine, runId);
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync().ConfigureAwait(false);
        for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, true); break; }
            catch { await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false); }
        }
    }
}

/// <summary>Throws when invoked; for plans without activity nodes.</summary>
internal sealed class UnusedActivity : IActivityExecutor
{
    public ValueTask<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException("No activity node exists in this parity plan.");
}

/// <summary>Throws when invoked; for plans without context nodes.</summary>
internal sealed class UnusedContext : IContextProvider
{
    public ValueTask<ContextExecutionResult> ExecuteAsync(
        ContextExecutionRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException("No context node exists in this parity plan.");
}

/// <summary>Throws when invoked; for plans without inference nodes.</summary>
internal sealed class UnusedInference : IInferenceExecutor, IInferenceExecutorPreflight
{
    public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement) => null;

    public ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException("No inference node exists in this parity plan.");
}
