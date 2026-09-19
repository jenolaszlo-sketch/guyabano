#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Planning;
using Guyabano.Llm.Prompting;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Spec §20 Test 6 (restart recovery): the process restarts while a forked
/// mutation is still pending. Planning artifacts are recovered with stable
/// revisions, the current workflow version is recovered, candidate state is
/// not confused with active state, and execution resumes correctly without
/// duplicating completed work.
/// </summary>
public sealed class RestartRecoveryMutationTests : IDisposable
{
    private const string WorkflowId = "plan";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-restart-recovery",
        Guid.NewGuid().ToString("N"));

    private static string Digest(char c) => new string(c, 64);

    private static TrustedCatalogueDescriptor ActivityDescriptor(
        string name, char digest, params string[] parameters)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new TrustedCatalogueDescriptor(
            new DescriptorReference(DescriptorKind.Activity, name, "1",
                new ContentDigest("sha256", "descriptor/v1", Digest(digest))),
            callableContract: new CallableContract(
                new CallableSignature([.. parameters.Select(p => new CallableParameter(p, str))], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe));
    }

    private static TrustedCatalogueDescriptor[] Descriptors() =>
    [
        ActivityDescriptor("sample.stage", 'a', "value"),
        ActivityDescriptor("sample.task", 'b', "value"),
    ];

    private static ITrustedCatalogue Catalogue() =>
        new InMemoryTrustedCatalogue(Descriptors());

    private static string V1Dsl() => $$"""
        workflow plan(input: string) -> string {
          activity domain = activity "sample.stage@1#{{Digest('a')}}" (value: input;) -> string;
          activity topology = activity "sample.stage@1#{{Digest('a')}}" (value: domain;) -> string;
          return topology;
        }
        """;

    private static string V2Dsl() => $$"""
        workflow plan(input: string) -> string {
          activity domain = activity "sample.stage@1#{{Digest('a')}}" (value: input;) -> string;
          activity topology = activity "sample.stage@1#{{Digest('a')}}" (value: domain;) -> string;
          activity implement = activity "sample.task@1#{{Digest('b')}}" (value: topology;) -> string;
          return implement;
        }
        """;

    [Fact]
    public async Task Restart_recovers_artifacts_versions_and_pending_mutation_then_resumes()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogue = Catalogue();
        var compiler = new WorkflowCompiler(
            catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", []));
        var activity = new TaskActivity();
        var databasePath = Path.Combine(_root, "workflow.db");
        var artifactRoot = Path.Combine(_root, "artifacts");
        Directory.CreateDirectory(_root);

        PlanningArtifactVersion domainVersion;
        PlanningArtifactVersion topologyVersion;
        string domainHash;
        string topologyHash;
        Guid v1Id;
        Guid v2Id;

        // v1 planning completes and its artifacts are published.
        var store1 = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            Pooling = false,
        });
        var registry1 = new WorkflowRegistry();
        await using (var engine1 = new WorkflowEngine(store1, registry1,
                         new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) }))
        {
            var admission1 = await AdmitDslAsync(V1Dsl(), catalogue, compiler, ct);
            var registration1 = await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    IdentityFor(admission1),
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateAsync(WorkflowId, "1", admission1, ct);
            registration1.Register(registry1);
            using var input = JsonDocument.Parse("\"goal\"");
            v1Id = await engine1.StartAsync(
                WorkflowId, "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine1.ExecuteAsync(v1Id, ct);
            await engine1.WaitForCompletionAsync<JsonElement>(v1Id, cancellationToken: ct);

            var catalog1 = new PlanningArtifactCatalog(new FileSystemArtifactRepository(artifactRoot));
            domainVersion = (await catalog1.PublishAsync(
                new PublishPlanningArtifactRequest<string>(
                    WorkflowId,
                    new PlanningArtifactKey("domain-discovery", "main"),
                    1, "plan-domain", "goal domain",
                    State: PlanningArtifactState.Valid), ct)).Version;
            topologyVersion = (await catalog1.PublishAsync(
                new PublishPlanningArtifactRequest<string>(
                    WorkflowId,
                    new PlanningArtifactKey("solution-topology", "main"),
                    1, "plan-topology", "goal topology",
                    Inputs: [domainVersion],
                    State: PlanningArtifactState.Valid), ct)).Version;
            domainHash = (await catalog1.GetAsync(WorkflowId, domainVersion, ct))!
                .Content.ContentHash;
            topologyHash = (await catalog1.GetAsync(WorkflowId, topologyVersion, ct))!
                .Content.ContentHash;

            // v2 is authored and forked but never executed: the pending mutation.
            var admission2 = await AuthorAsync(V2Dsl(), ct);
            var registration2 = await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    IdentityFor(admission2),
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference())
                    {
                        PriorExecutionFingerprints = new HashSet<string>(
                            [admission1.Receipt!.ExecutionFingerprint], StringComparer.Ordinal),
                    })
                .CreateAsync(WorkflowId, "2", admission2, ct);
            registration2.Register(registry1);
            v2Id = await engine1.ForkAsync(
                v1Id, "plan/implement",
                new ForkRunOptions { TargetWorkflowVersion = "2", Actor = "test", Reason = "start implementation" },
                ct);
            (await engine1.GetRunAsync(v2Id, ct))!.Status.Should().Be(WorkflowStatus.Pending);

            // A candidate for a future mutation exists, but it is not active.
            await catalog1.PublishAsync(
                new PublishPlanningArtifactRequest<string>(
                    WorkflowId,
                    new PlanningArtifactKey("fuwen-candidate", "main"),
                    1, "author-fuwen", V2Dsl(),
                    Inputs: [topologyVersion],
                    State: PlanningArtifactState.Candidate),
                ct);
        }

        // Process restart: drop every in-memory handle and rebuild from disk.
        var store2 = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            Pooling = false,
        });
        var registry2 = new WorkflowRegistry();
        await using (var engine2 = new WorkflowEngine(store2, registry2,
                         new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) }))
        {
            // Hosts re-admit and re-register definitions on startup.
            var admission1 = await AdmitDslAsync(V1Dsl(), catalogue, compiler, ct);
            (await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    IdentityFor(admission1),
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateAsync(WorkflowId, "1", admission1, ct)).Register(registry2);
            var admission2 = await AuthorAsync(V2Dsl(), ct);
            (await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    IdentityFor(admission2),
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference())
                    {
                        PriorExecutionFingerprints = new HashSet<string>(
                            [admission1.Receipt!.ExecutionFingerprint], StringComparer.Ordinal),
                    })
                .CreateAsync(WorkflowId, "2", admission2, ct)).Register(registry2);

            // Planning artifacts are recovered with stable revisions.
            var catalog2 = new PlanningArtifactCatalog(
                new FileSystemArtifactRepository(artifactRoot));
            var domain = await catalog2.GetAsync(WorkflowId, domainVersion, ct);
            domain.Should().NotBeNull();
            domain!.Content.ContentHash.Should().Be(domainHash);
            domain.State.Should().Be(PlanningArtifactState.Valid);
            var topology = await catalog2.GetAsync(WorkflowId, topologyVersion, ct);
            topology.Should().NotBeNull();
            topology!.Content.ContentHash.Should().Be(topologyHash);
            var domainPayload = await catalog2.ReadPayloadAsync<string>(domain, ct);
            domainPayload.Should().Be("goal domain");

            // The current workflow version is recovered; the candidate is not active.
            (await engine2.GetRunAsync(v1Id, ct))!.Status.Should().Be(WorkflowStatus.Completed);
            var recovered = await engine2.GetRunAsync(v2Id, ct);
            recovered!.WorkflowVersion.Should().Be("2");
            recovered.SourceRunId.Should().Be(v1Id);
            recovered.Status.Should().Be(WorkflowStatus.Pending);
            var candidate = await catalog2.GetCurrentAsync(
                WorkflowId, new PlanningArtifactKey("fuwen-candidate", "main"), ct);
            candidate!.State.Should().Be(PlanningArtifactState.Candidate);

            // Execution resumes: planning evidence is reused, only implement runs.
            await engine2.ExecuteAsync(v2Id, ct);
            var output = await engine2.WaitForCompletionAsync<JsonElement>(v2Id, cancellationToken: ct);
            output.GetString()!.Should().EndWith(".implement");
            (await engine2.GetRunAsync(v2Id, ct))!.Status.Should().Be(WorkflowStatus.Completed);
        }

        CallsFor(activity, "domain").Should().HaveCount(1);
        CallsFor(activity, "topology").Should().HaveCount(1);
        CallsFor(activity, "implement").Should().HaveCount(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            for (var i = 0; i < 5; i++)
            {
                try { Directory.Delete(_root, true); break; }
                catch { Thread.Sleep(50 * (i + 1)); }
            }
        }
    }

    private static FuwenZhinuProviderRuntimeIdentity IdentityFor(WorkflowAdmissionResult admission) =>
        new(
            admission.Receipt!.CatalogueSnapshotRevision,
            admission.Receipt.ResolvedDescriptorSetFingerprint);

    private static async Task<WorkflowAdmissionResult> AdmitDslAsync(
        string dsl, ITrustedCatalogue catalogue, WorkflowCompiler compiler, CancellationToken ct)
    {
        var compiled = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(dsl, cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(
            string.Join("; ", compiled.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
        var admission = await new WorkflowAdmissionService(compiler)
            .AdmitAsync(compiled.Plan!, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
        return admission;
    }

    private async Task<WorkflowAdmissionResult> AuthorAsync(string dsl, CancellationToken ct)
    {
        var author = new WorkflowAuthor(
            new CannedPlanRouter(dsl),
            new WorkflowAuthoringPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()))),
            maxAttempts: 1);
        var result = await author.AuthorFromPlanAsync(
            "Implement the plan.",
            "Translate the resolved plan.",
            CatalogueSummaryBuilder.Render(Descriptors()),
            Catalogue(),
            "stub-author",
            4000,
            ct);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Admission.Should().NotBeNull();
        return result.Admission!;
    }

    private static IReadOnlyList<string> CallsFor(TaskActivity activity, string node) =>
        activity.Calls.Where(call => call.StartsWith(node + ":", StringComparison.Ordinal)).ToArray();

    private static string FindPromptsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "prompts", "workflow-authoring", "system.sbn");
            if (File.Exists(candidate))
                return Path.Combine(directory.FullName, "prompts");
        }
        throw new DirectoryNotFoundException("Could not locate the Guyabano prompts root.");
    }

    private sealed class TaskActivity : IActivityExecutor
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var node = request.Invocation.StructuralPath.Split('/')[^1];
            var input = ((JsonRuntimeValue)request.Arguments.Single(a => a.Name == "value").Value)
                .Value.GetString()!;
            Calls.Add($"{node}:{input}");
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(input + "." + node));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
                RuntimeValue.FromJson(document.RootElement.Clone())));
        }
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest r, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class UnusedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest r, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class CannedPlanRouter(string dsl) : ILlmRouter
    {
        public List<LlmRequest> Requests { get; } = [];

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return StreamSingle(dsl);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ModelStrategy strategy, LlmRequest request, CancellationToken cancellationToken) =>
            StreamAsync(strategy.ToString(), request, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(model, builder.Build(ModelStrategy.Auto), cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ModelStrategy strategy, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(strategy.ToString(), builder, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
            string route, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(route, builder, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
            string route, LlmRequest request, CancellationToken cancellationToken) =>
            StreamAsync(route, request, cancellationToken);

        public ResolvedEndpoint Resolve(string model) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(string model, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ResolvedEndpoint Resolve(ModelStrategy strategy) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(ModelStrategy strategy, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveRouteAsync(string route, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainModelAsync(string model, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainStrategyAsync(ModelStrategy strategy, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainRouteAsync(string route, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        private static async IAsyncEnumerable<LlmStreamEvent> StreamSingle(
            string delta,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new LlmStreamEvent(delta, null, "stop", null, null, null, null, null, null);
        }
    }
}
