#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Zhinu-backed execution host: real sqlite workflows, versioned
/// definitions, fork-based revisions with fingerprint reuse, and evidence.
/// </summary>
public sealed class ZhinuPlanningExecutionHostTests : IDisposable
{
    private const string WorkflowId = "host-workflow-1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-zhinu-host-tests",
        Guid.NewGuid().ToString("N"));

    private static string Digest(char c) => new string(c, 64);

    private static ITrustedCatalogue Catalogue()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        TrustedCatalogueDescriptor Descriptor(string version, char digest) => new(
            new DescriptorReference(
                DescriptorKind.Activity, "guyabano.execute", version,
                new ContentDigest("sha256", "descriptor/v1", Digest(digest))),
            callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("task", str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe));
        return new InMemoryTrustedCatalogue([Descriptor("1", 'a'), Descriptor("2", 'b')]);
    }

    private static string ActivityRef(string version, char digest) =>
        $"guyabano.execute@{version}#{Digest(digest)}";

    private static string V1Dsl() => $$"""
        workflow implementation(input: string) -> string {
          activity implement_a = activity "{{ActivityRef("1", 'a')}}" (task: input;) -> string;
          activity implement_b = activity "{{ActivityRef("1", 'a')}}" (task: implement_a;) -> string;
          return implement_b;
        }
        """;

    private static string V2AddDsl() => $$"""
        workflow implementation(input: string) -> string {
          activity implement_a = activity "{{ActivityRef("1", 'a')}}" (task: input;) -> string;
          activity implement_b = activity "{{ActivityRef("1", 'a')}}" (task: implement_a;) -> string;
          activity implement_c = activity "{{ActivityRef("1", 'a')}}" (task: implement_b;) -> string;
          return implement_c;
        }
        """;

    private static string V3AddDsl() => $$"""
        workflow implementation(input: string) -> string {
          activity implement_a = activity "{{ActivityRef("1", 'a')}}" (task: input;) -> string;
          activity implement_b = activity "{{ActivityRef("1", 'a')}}" (task: implement_a;) -> string;
          activity implement_c = activity "{{ActivityRef("1", 'a')}}" (task: implement_b;) -> string;
          activity implement_d = activity "{{ActivityRef("1", 'a')}}" (task: implement_c;) -> string;
          return implement_d;
        }
        """;

    private static string V2ReplaceDsl() => $$"""
        workflow implementation(input: string) -> string {
          activity implement_a = activity "{{ActivityRef("1", 'a')}}" (task: input;) -> string;
          activity implement_b = activity "{{ActivityRef("2", 'b')}}" (task: implement_a;) -> string;
          return implement_b;
        }
        """;

    private static WorkflowPatch AddPatch() => new()
    {
        BaseDesignFingerprint = "test-base",
        DerivedFromArtifacts = ["contracts/billing@2"],
        Rationale = "Add cache implementation.",
        AddSteps =
        [
            new PlannedExecutionStep
            {
                Id = "implement_c",
                Title = "Implement cache",
                DependsOn = ["implement_b"],
                RequiredArtifacts = [],
                AcceptanceCriteria = [],
            },
        ],
        ReplaceSteps = [],
        RemoveStepIds = [],
        AddBindings =
        [
            new PlannedNodeBinding
            {
                StepId = "implement_c",
                Binding = new PlannedExecutionBinding
                {
                    Role = "implement",
                    Capability = "code.modify",
                    ModelProfile = "implementation",
                    ContextArtifacts = [],
                    Descriptor = new DescriptorReference(
                        DescriptorKind.Activity, "guyabano.execute", "1",
                        new ContentDigest("sha256", "descriptor/v1", Digest('a'))),
                },
            },
        ],
        ReplaceBindings = [],
        DependencyEdits = [],
    };

    private static WorkflowPatch ReplacePatch() => new()
    {
        BaseDesignFingerprint = "test-base",
        DerivedFromArtifacts = ["contracts/billing@2"],
        Rationale = "Use the v2 executor for billing.",
        AddSteps = [],
        ReplaceSteps = [],
        RemoveStepIds = [],
        AddBindings = [],
        ReplaceBindings =
        [
            new PlannedNodeBinding
            {
                StepId = "implement_b",
                Binding = new PlannedExecutionBinding
                {
                    Role = "implement",
                    Capability = "code.modify",
                    ModelProfile = "implementation",
                    ContextArtifacts = [],
                    Descriptor = new DescriptorReference(
                        DescriptorKind.Activity, "guyabano.execute", "2",
                        new ContentDigest("sha256", "descriptor/v1", Digest('b'))),
                },
            },
        ],
        DependencyEdits = [],
    };

    private static WorkflowPatch AddNodePatch() => new()
    {
        BaseDesignFingerprint = "test-base",
        DerivedFromArtifacts = ["contracts/billing@2"],
        Rationale = "Add deployment.",
        AddSteps =
        [
            new PlannedExecutionStep
            {
                Id = "implement_d",
                Title = "Implement deploy",
                DependsOn = ["implement_c"],
                RequiredArtifacts = [],
                AcceptanceCriteria = [],
            },
        ],
        ReplaceSteps = [],
        RemoveStepIds = [],
        AddBindings =
        [
            new PlannedNodeBinding
            {
                StepId = "implement_d",
                Binding = new PlannedExecutionBinding
                {
                    Role = "implement",
                    Capability = "code.modify",
                    ModelProfile = "implementation",
                    ContextArtifacts = [],
                    Descriptor = new DescriptorReference(
                        DescriptorKind.Activity, "guyabano.execute", "1",
                        new ContentDigest("sha256", "descriptor/v1", Digest('a'))),
                },
            },
        ],
        ReplaceBindings = [],
        DependencyEdits = [],
    };

    private static WorkflowPatch EmptyPatch() => new()
    {
        BaseDesignFingerprint = "test-base",
        DerivedFromArtifacts = ["contracts/billing@2"],
        Rationale = "No workflow change.",
        AddSteps = [],
        ReplaceSteps = [],
        RemoveStepIds = [],
        AddBindings = [],
        ReplaceBindings = [],
        DependencyEdits = [],
    };

    [Fact]
    public async Task Ensure_creates_executes_and_observes_v1()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_root, ct);
        var host = harness.Host(Catalogue());

        var snapshot = await host.EnsureWorkflowAsync(
            WorkflowId, V1Dsl(), JsonSerializer.Serialize("goal"), ct);

        snapshot.WorkflowVersion.Should().Be("1");
        snapshot.IsComplete.Should().BeTrue();
        snapshot.EvidenceSummary.Should().Contain("completed");
        harness.Activity.Calls.Select(CallName).Should().Equal("implement_a", "implement_b");

        var observed = await host.ObserveAsync(WorkflowId, ct);
        observed.Should().BeEquivalentTo(snapshot);

        var ensured = await host.EnsureWorkflowAsync(
            WorkflowId, V1Dsl(), JsonSerializer.Serialize("goal"), ct);
        ensured.WorkflowVersion.Should().Be("1");
        harness.Activity.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteRevision_forks_runs_only_new_nodes_and_reports()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_root, ct);
        var host = harness.Host(Catalogue());
        await host.EnsureWorkflowAsync(WorkflowId, V1Dsl(), JsonSerializer.Serialize("goal"), ct);

        var result = await host.ExecuteRevisionAsync(WorkflowId, V2AddDsl(), AddPatch(), ct);

        result.WorkflowVersion.Should().Be("2");
        result.EvidenceSummary.Should().Contain("implement_c");
        result.EvidenceSummary.Should().Contain("completed");
        harness.Activity.Calls.Select(CallName).Should()
            .Equal("implement_a", "implement_b", "implement_c");

        var observed = await host.ObserveAsync(WorkflowId, ct);
        observed.WorkflowVersion.Should().Be("2");
        observed.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteRevision_reruns_changed_nodes_but_preserves_siblings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_root, ct);
        var host = harness.Host(Catalogue());
        await host.EnsureWorkflowAsync(WorkflowId, V1Dsl(), JsonSerializer.Serialize("goal"), ct);

        var result = await host.ExecuteRevisionAsync(WorkflowId, V2ReplaceDsl(), ReplacePatch(), ct);

        result.WorkflowVersion.Should().Be("2");
        harness.Activity.Calls.Select(CallName).Should()
            .Equal("implement_a", "implement_b", "implement_b");
    }

    [Fact]
    public async Task Observe_recovers_after_host_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_root, ct);
        var host = harness.Host(Catalogue());
        await host.EnsureWorkflowAsync(WorkflowId, V1Dsl(), JsonSerializer.Serialize("goal"), ct);
        await host.ExecuteRevisionAsync(WorkflowId, V2AddDsl(), AddPatch(), ct);

        await using var restarted = await Harness.ReopenAsync(_root, harness.Activity, ct);
        var recovered = restarted.Host(Catalogue());

        var observed = await recovered.ObserveAsync(WorkflowId, ct);
        observed.WorkflowVersion.Should().Be("2");
        observed.IsComplete.Should().BeTrue();

        await recovered.RegisterAsync(WorkflowId, "2", V2AddDsl(), ct);
        var result = await recovered.ExecuteRevisionAsync(WorkflowId, V3AddDsl(), AddNodePatch(), ct);
        result.WorkflowVersion.Should().Be("3");
        result.EvidenceSummary.Should().Contain("implement_d");
        harness.Activity.Calls.Select(CallName).Should()
            .Equal("implement_a", "implement_b", "implement_c", "implement_d");
    }

    [Fact]
    public async Task ExecuteRevision_skips_executable_no_ops()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_root, ct);
        var host = harness.Host(Catalogue());
        await host.EnsureWorkflowAsync(WorkflowId, V1Dsl(), JsonSerializer.Serialize("goal"), ct);

        var result = await host.ExecuteRevisionAsync(WorkflowId, V1Dsl(), EmptyPatch(), ct);

        result.WorkflowVersion.Should().Be("1");
        result.EvidenceSummary.Should().Contain("no executable change");
        harness.Activity.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteRevision_preserves_grandparent_evidence_across_generations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_root, ct);
        var host = harness.Host(Catalogue());
        await host.EnsureWorkflowAsync(WorkflowId, V1Dsl(), JsonSerializer.Serialize("goal"), ct);
        await host.ExecuteRevisionAsync(WorkflowId, V2AddDsl(), AddPatch(), ct);

        var result = await host.ExecuteRevisionAsync(WorkflowId, V3AddDsl(), AddNodePatch(), ct);

        result.WorkflowVersion.Should().Be("3");
        harness.Activity.Calls.Select(CallName).Should()
            .Equal("implement_a", "implement_b", "implement_c", "implement_d");
    }

    private static string CallName(string structuralPath) =>
        structuralPath[(structuralPath.LastIndexOf('/') + 1)..];

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly WorkflowEngine _engine;

        private Harness(
            WorkflowEngine engine,
            WorkflowRegistry registry,
            RecordingActivity activity)
        {
            _engine = engine;
            Registry = registry;
            Activity = activity;
        }

        public WorkflowRegistry Registry { get; }

        public RecordingActivity Activity { get; }

        public static Task<Harness> CreateAsync(string root, CancellationToken ct)
        {
            Directory.CreateDirectory(root);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            var registry = new WorkflowRegistry();
            var engine = new WorkflowEngine(
                store, registry, new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            return Task.FromResult(new Harness(engine, registry, new RecordingActivity()));
        }

        public static Task<Harness> ReopenAsync(
            string root, RecordingActivity activity, CancellationToken ct)
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            var registry = new WorkflowRegistry();
            var engine = new WorkflowEngine(
                store, registry, new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            return Task.FromResult(new Harness(engine, registry, activity));
        }

        public ZhinuPlanningExecutionHost Host(ITrustedCatalogue catalogue) =>
            new(_engine, Registry, new InMemoryWorkflowDefinitionStore(),
                Activity, new UnusedContext(), new UnusedInference(), catalogue);

        public async ValueTask DisposeAsync() => await _engine.DisposeAsync();
    }

    private sealed class RecordingActivity : IActivityExecutor
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Invocation.StructuralPath);
            var task = request.Arguments
                .Select(argument => RuntimeValueJson.ToJsonElement(argument.Value).GetString())
                .FirstOrDefault() ?? string.Empty;
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement($"done-{task}"))));
        }
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(
            ContextExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class UnusedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
