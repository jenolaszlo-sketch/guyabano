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

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Gap C driver: the host loop observes, decides, acts through the patch
/// pipeline, checkpoints, and enforces policy bounds with typed outcomes.
/// Scripted deciders and hosts stand in for models and Zhinu.
/// </summary>
public sealed class PlanningLoopTests : IDisposable
{
    private const string WorkflowId = "planning-loop-1";
    private const string Goal = "Use the v2 generator for billing.";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-planning-loop-tests",
        Guid.NewGuid().ToString("N"));

    private static string Digest(char c) => new string(c, 64);

    private sealed record ContractPayload(string Name);

    private static ContentDigest ContentDigest(char c) =>
        new("sha256", "descriptor/v1", Digest(c));

    private static DescriptorReference ActivityRef(string version, char digest) =>
        new(DescriptorKind.Activity, "guyabano.execute", version, ContentDigest(digest));

    private static PlannedExecutionDesign Design()
    {
        var steps = new[]
        {
            new PlannedExecutionStep
            {
                Id = "implement_a",
                Title = "Implement todos",
                DependsOn = [],
                RequiredArtifacts = [],
                AcceptanceCriteria = [],
            },
            new PlannedExecutionStep
            {
                Id = "implement_billing",
                Title = "Implement billing",
                DependsOn = ["implement_a"],
                RequiredArtifacts = ["contracts/billing@1"],
                AcceptanceCriteria = [],
            },
        };
        var bindings = new[]
        {
            NodeBinding("implement_a", "1", 'a', []),
            NodeBinding("implement_billing", "1", 'a', ["contracts/billing@1"]),
        };
        return new PlannedExecutionDesign(
            new PlannedExecutionGraph
            {
                WorkflowName = "implementation",
                InputType = "string",
                OutputType = "string",
                Steps = steps,
            },
            new PlannedExecutionBindings
            {
                WorkflowName = "implementation",
                Nodes = bindings,
            });
    }

    private static PlannedNodeBinding NodeBinding(
        string stepId, string version, char digest, string[] contextArtifacts) => new()
        {
            StepId = stepId,
            Binding = new PlannedExecutionBinding
            {
                Role = "implement",
                Capability = "code.modify",
                ModelProfile = "implementation",
                ContextArtifacts = contextArtifacts,
                Descriptor = ActivityRef(version, digest),
            },
        };

    private static string PriorDsl() => $$"""
        workflow implementation(input: string) -> string {
          activity implement_a = activity "guyabano.execute@1#{{Digest('a')}}" (task: input;) -> string;
          activity implement_billing = activity "guyabano.execute@1#{{Digest('a')}}" (task: implement_a;) -> string;
          return implement_billing;
        }
        """;

    private static string CandidateDsl() => PriorDsl().Replace(
        $"guyabano.execute@1#{Digest('a')}\" (task: implement_a;)",
        $"guyabano.execute@2#{Digest('b')}\" (task: implement_a;)",
        StringComparison.Ordinal);

    private static WorkflowPatch BillingV2Patch(PlannedExecutionDesign design)
    {
        var billing = design.Bindings.Nodes.Single(node => node.StepId == "implement_billing");
        return new WorkflowPatch
        {
            BaseDesignFingerprint = StagedExecutionDesignIdentity.Compute(design),
            DerivedFromArtifacts = ["contracts/billing@2"],
            Rationale = "Use the v2 generator for billing.",
            AddSteps = [],
            ReplaceSteps = [],
            RemoveStepIds = [],
            AddBindings = [],
            ReplaceBindings =
            [
                billing with
                {
                    Binding = billing.Binding with { Descriptor = ActivityRef("2", 'b') },
                },
            ],
            DependencyEdits = [],
        };
    }

    private static WorkflowPatch EmptyPatch(PlannedExecutionDesign design) =>
        BillingV2Patch(design) with
        {
            Rationale = "No workflow change required.",
            ReplaceBindings = [],
        };

    private static ITrustedCatalogue CompileCatalogue()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        TrustedCatalogueDescriptor Descriptor(string version, char digest) => new(
            ActivityRef(version, digest),
            callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("task", str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe));
        return new InMemoryTrustedCatalogue([Descriptor("1", 'a'), Descriptor("2", 'b')]);
    }

    private static PlanningDecision ExpandFor(
        PlanningLoopObservation observation, params string[] motivating) => new()
        {
            DesignFingerprint = observation.DesignFingerprint,
            ArtifactRevisions = observation.FreshArtifactRevisions,
            WorkflowVersion = observation.WorkflowVersion,
            Action = PlanningAction.Expand,
            MotivatingArtifacts = motivating,
            Rationale = "Absorb the revised billing contract.",
        };

    private static PlanningDecision FinishFor(PlanningLoopObservation observation, string reason) => new()
    {
        DesignFingerprint = observation.DesignFingerprint,
        ArtifactRevisions = observation.FreshArtifactRevisions,
        WorkflowVersion = observation.WorkflowVersion,
        Action = PlanningAction.Finish,
        MotivatingArtifacts = [],
        Rationale = "Goal met.",
        FinishReason = reason,
    };

    [Fact]
    public async Task Loop_expands_once_then_finishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var harness = await CreateHarnessAsync(ct);
        var decider = new ScriptedDecider(
        [
            obs => ExpandFor(obs, "contracts/billing@2"),
            obs => FinishFor(obs, "Billing uses v2; goal met."),
        ]);
        var loop = harness.Loop(
            decider,
            proposerScript: [JsonSerializer.Serialize(BillingV2Patch(design))],
            authorScript: [CandidateDsl()]);

        var outcome = await loop.RunAsync(
            WorkflowId, Goal, design, PriorDsl(),
            CompileCatalogue(), "activity guyabano.execute@1#aaa", "stub", 4000, ct);

        outcome.Status.Should().Be(PlanningLoopStatus.Finished);
        outcome.Reason.Should().Contain("goal met");
        outcome.Checkpoint.Mutations.Should().Be(1);
        outcome.Checkpoint.StructuralIterations.Should().Be(1);
        outcome.Checkpoint.ModelCalls.Should().Be(4);
        outcome.Checkpoint.Iteration.Should().Be(1);
        outcome.Checkpoint.DecisionLog.Should().HaveCount(2);
        outcome.FinalDesign.Bindings.Nodes.Single(node => node.StepId == "implement_billing")
            .Binding.Descriptor.Version.Should().Be("2");
        harness.Host.Executions.Should().Be(1);

        // Bootstrap reports pre-existing revisions as fresh and flags the
        // design pin the catalog has moved past.
        decider.Observations.Should().HaveCount(2);
        var first = decider.Observations[0];
        first.FreshArtifactRevisions.Should().ContainSingle()
            .Which.Should().Be("contracts/billing@2");
        first.SupersededPins.Should().ContainSingle()
            .Which.Should().Be("contracts/billing@2 supersedes pinned contracts/billing@1");
    }

    [Fact]
    public async Task Loop_stops_at_max_iterations()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var harness = await CreateHarnessAsync(ct, PlanningPolicy.Default with { MaxIterations = 1 });
        var decider = new ScriptedDecider([obs => ExpandFor(obs, "contracts/billing@2")]);
        var loop = harness.Loop(
            decider,
            proposerScript: [JsonSerializer.Serialize(BillingV2Patch(design))],
            authorScript: [CandidateDsl()]);

        var outcome = await loop.RunAsync(
            WorkflowId, Goal, design, PriorDsl(),
            CompileCatalogue(), "activity guyabano.execute@1#aaa", "stub", 4000, ct);

        outcome.Status.Should().Be(PlanningLoopStatus.Exhausted);
        outcome.Reason.Should().Contain("Max iterations");
        outcome.Checkpoint.Mutations.Should().Be(1);
        harness.Host.Executions.Should().Be(1);
    }

    [Fact]
    public async Task Loop_converges_on_consecutive_no_ops()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var harness = await CreateHarnessAsync(ct);
        var emptyJson = JsonSerializer.Serialize(EmptyPatch(design));
        var decider = new ScriptedDecider(
        [
            obs => ExpandFor(obs, "contracts/billing@2"),
            obs => ExpandFor(obs, "contracts/billing@2"),
        ]);
        var loop = harness.Loop(
            decider,
            proposerScript: [emptyJson, emptyJson],
            authorScript: [CandidateDsl()]);

        var outcome = await loop.RunAsync(
            WorkflowId, Goal, design, PriorDsl(),
            CompileCatalogue(), "activity guyabano.execute@1#aaa", "stub", 4000, ct);

        outcome.Status.Should().Be(PlanningLoopStatus.Finished);
        outcome.Reason.Should().Contain("No-op convergence");
        outcome.Checkpoint.ConsecutiveNoOps.Should().Be(2);
        outcome.Checkpoint.Mutations.Should().Be(0);
        harness.Host.Executions.Should().Be(0);
        harness.AuthorRouter.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Loop_resumes_after_crash_rejects_divergence_and_replays_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var patchJson = JsonSerializer.Serialize(BillingV2Patch(design));
        var harness = await CreateHarnessAsync(ct);

        var crashingDecider = new ScriptedDecider(
        [
            obs => ExpandFor(obs, "contracts/billing@2"),
            obs => throw new InvalidOperationException("simulated crash"),
        ]);
        var crashingLoop = harness.Loop(
            crashingDecider, [patchJson], [CandidateDsl()]);
        var crashed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            crashingLoop.RunAsync(
                WorkflowId, Goal, design, PriorDsl(),
                CompileCatalogue(), "activity guyabano.execute@1#aaa", "stub", 4000, ct));
        crashed.Message.Should().Contain("simulated crash");
        harness.Host.Executions.Should().Be(1);

        var movedLoop = harness.Loop(new ScriptedDecider([]), [], []);
        harness.Host.Version = "v9";
        var diverged = await movedLoop.RunAsync(
            WorkflowId, Goal, design, PriorDsl(),
            CompileCatalogue(), "activity guyabano.execute@1#aaa", "stub", 4000, ct);
        diverged.Status.Should().Be(PlanningLoopStatus.Failed);
        diverged.Reason.Should().Contain("moved");

        harness.Host.Version = "v2";
        var finishingLoop = harness.Loop(
            new ScriptedDecider([obs => FinishFor(obs, "Resumed and done.")]), [], []);
        var resumed = await finishingLoop.RunAsync(
            WorkflowId, Goal, design, PriorDsl(),
            CompileCatalogue(), "activity guyabano.execute@1#aaa", "stub", 4000, ct);
        resumed.Status.Should().Be(PlanningLoopStatus.Finished);
        resumed.Checkpoint.Iteration.Should().Be(1);
        harness.Host.Executions.Should().Be(1);

        var replayLoop = harness.Loop(new ScriptedDecider([]), [], []);
        var replayed = await replayLoop.RunAsync(
            WorkflowId, Goal, design, PriorDsl(),
            CompileCatalogue(), "activity guyabano.execute@1#aaa", "stub", 4000, ct);
        replayed.Status.Should().Be(PlanningLoopStatus.Finished);
        replayed.Reason.Should().Contain("Resumed and done.");
    }

    [Fact]
    public async Task Loop_reobserves_on_a_stale_basis()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var harness = await CreateHarnessAsync(ct);
        var decider = new ScriptedDecider(
        [
            obs => ExpandFor(obs, "contracts/billing@2") with { DesignFingerprint = "stale" },
            obs => FinishFor(obs, "Fresh basis; nothing to do."),
        ]);
        var loop = harness.Loop(decider, [], []);

        var outcome = await loop.RunAsync(
            WorkflowId, Goal, design, PriorDsl(),
            CompileCatalogue(), "activity guyabano.execute@1#aaa", "stub", 4000, ct);

        outcome.Status.Should().Be(PlanningLoopStatus.Finished);
        decider.Calls.Should().Be(2);
        harness.Host.Observes.Should().Be(2);
        outcome.Checkpoint.ModelCalls.Should().Be(2);
    }

    [Fact]
    public async Task Decision_pack_renders_the_observation()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var fingerprint = StagedExecutionDesignIdentity.Compute(design);

        var builder = new PlanningDecisionPromptBuilder(
            new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot())));
        var request = await builder.BuildAsync(
            new PlanningDecisionPromptContext(
                Goal,
                PlannedExecutionDesignSummary.Render(design),
                fingerprint,
                "v1",
                false,
                "nothing executed yet",
                ["contracts/billing@2"],
                ["contracts/billing@2 supersedes pinned contracts/billing@1"],
                9, 5, 8, 29, null, 2000),
            ct);

        var systemText = TextOf(request, "system");
        var userText = TextOf(request, "user");
        systemText.Should().Contain(fingerprint);
        systemText.Should().Contain("contracts/billing@2");
        systemText.Should().Contain("supersedes pinned contracts/billing@1");
        systemText.Should().Contain("implement_billing");
        systemText.Should().Contain("Iterations: 9");
        userText.Should().Contain(Goal);
        userText.Should().Contain("exactly one JSON object");
    }

    [Fact]
    public async Task Llm_decider_parses_a_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var fingerprint = StagedExecutionDesignIdentity.Compute(design);
        var decision = new PlanningDecision
        {
            DesignFingerprint = fingerprint,
            ArtifactRevisions = ["contracts/billing@2"],
            WorkflowVersion = "v1",
            Action = PlanningAction.Expand,
            MotivatingArtifacts = ["contracts/billing@2"],
            Rationale = "Absorb the revision.",
        };

        var router = new ScriptedRouter([JsonSerializer.Serialize(decision)]);
        var decider = new LlmPlanningDecider(
            router,
            new PlanningDecisionPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()))),
            "stub-decider");
        var observation = new PlanningLoopObservation(
            Goal,
            PlannedExecutionDesignSummary.Render(design),
            fingerprint,
            ["contracts/billing@2"],
            ["contracts/billing@2 supersedes pinned contracts/billing@1"],
            "v1",
            false,
            "nothing executed yet",
            new PlanningLoopBudget(9, 5, 8, 29, null),
            null);

        var result = await decider.DecideAsync(observation, ct);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Decision!.Action.Should().Be(PlanningAction.Expand);
        result.ModelCalls.Should().Be(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<Harness> CreateHarnessAsync(
        CancellationToken ct, PlanningPolicy? policy = null)
    {
        var catalog = new PlanningArtifactCatalog(new FileSystemArtifactRepository(_root));
        await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<ContractPayload>(
                WorkflowId,
                new PlanningArtifactKey("contracts", "billing"),
                1,
                "test",
                new ContractPayload("billing-v1")),
            ct);
        await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<ContractPayload>(
                WorkflowId,
                new PlanningArtifactKey("contracts", "billing"),
                1,
                "test",
                new ContractPayload("billing-v2")),
            ct);
        return new Harness(catalog, policy ?? PlanningPolicy.Default);
    }

    private sealed class Harness(PlanningArtifactCatalog catalog, PlanningPolicy policy)
    {
        public ScriptedHost Host { get; } = new();
        public ScriptedRouter ProposerRouter { get; private set; } = null!;
        public ScriptedRouter AuthorRouter { get; private set; } = null!;

        public PlanningLoop Loop(
            ScriptedDecider decider,
            IReadOnlyList<string> proposerScript,
            IReadOnlyList<string> authorScript)
        {
            var engine = new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()));
            ProposerRouter = new ScriptedRouter(proposerScript);
            AuthorRouter = new ScriptedRouter(authorScript);
            return new PlanningLoop(
                decider,
                new WorkflowPatchProposer(ProposerRouter, new WorkflowPatchPromptBuilder(engine), 3),
                new WorkflowAuthor(AuthorRouter, new WorkflowAuthoringPromptBuilder(engine), 3),
                Host,
                catalog,
                policy);
        }
    }

    private sealed class ScriptedDecider(
        IReadOnlyList<Func<PlanningLoopObservation, PlanningDecision>> script) : IPlanningDecider
    {
        private int next;
        public int Calls { get; private set; }
        public List<PlanningLoopObservation> Observations { get; } = [];

        public Task<PlanningDecisionResult> DecideAsync(
            PlanningLoopObservation observation,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Observations.Add(observation);
            var decision = script[Math.Min(next, script.Count - 1)](observation);
            next++;
            return Task.FromResult(new PlanningDecisionResult(true, decision, 1, []));
        }
    }

    private sealed class ScriptedHost : IPlanningExecutionHost
    {
        public string Version = "v1";
        public int Observes { get; private set; }
        public int Executions { get; private set; }

        public Task<WorkflowExecutionSnapshot> ObserveAsync(
            string workflowId,
            CancellationToken cancellationToken = default)
        {
            Observes++;
            return Task.FromResult(new WorkflowExecutionSnapshot(Version, false, "evidence-so-far"));
        }

        public Task<RevisionExecutionResult> ExecuteRevisionAsync(
            string workflowId,
            PlannedExecutionDesign applied,
            string dsl,
            WorkflowPatch patch,
            CancellationToken cancellationToken = default)
        {
            Executions++;
            Version = "v2";
            return Task.FromResult(new RevisionExecutionResult(Version, "revision executed", null));
        }
    }

    private static string TextOf(LlmRequest request, string role) => string.Concat(request.Messages
        .Where(m => m.Role == role)
        .SelectMany(m => m.Parts)
        .OfType<LlmTextContent>()
        .Select(p => p.Text));

    private static string FindPromptsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "prompts", "planning-decision", "system.sbn");
            if (File.Exists(candidate))
                return Path.Combine(directory.FullName, "prompts");
        }
        throw new DirectoryNotFoundException("Could not locate the Guyabano prompts root.");
    }

    private sealed class ScriptedRouter(IReadOnlyList<string> script) : ILlmRouter
    {
        private int next;
        public List<LlmRequest> Requests { get; } = [];

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var index = Math.Min(next, script.Count - 1);
            next++;
            return StreamSingle(script[index]);
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
