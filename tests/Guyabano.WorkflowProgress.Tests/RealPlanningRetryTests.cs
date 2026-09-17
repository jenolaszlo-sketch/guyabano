#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Guyabano.Llm.Prompting;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Nuwa;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Slice 2 of real Guyabano behavior in Fuwen: the staged service's bounded
/// stage-retry loop (max 3 attempts, validation feedback via
/// <c>previousFailure</c>) implemented as a Fuwen repeat loop around the
/// real domain-discovery inference node in envelope mode.
/// </summary>
public sealed class RealPlanningRetryTests
{
    private const string Model = "deepseek-v4-flash";
    private const int MaxTokens = 8000;

    private const string ValidDomainJson = """
        {
          "mission": {
            "guidingIntent": "Let users manage a todo list.",
            "successOutcomes": ["Todos can be added and completed."],
            "constraints": [],
            "nonGoals": []
          },
          "title": "Todo planner",
          "summary": "Plan a minimal todo application.",
          "terms": [{ "name": "Todo", "definition": "A single trackable task." }],
          "capabilities": [{ "name": "ManageTodos", "description": "CRUD for todos.", "businessRules": [] }],
          "useCases": [{
            "name": "AddTodo",
            "capabilityName": "ManageTodos",
            "actor": "User",
            "objective": "Add a todo.",
            "preconditions": [],
            "inputs": [],
            "businessRules": [],
            "outcomes": ["Todo is stored."],
            "errorOutcomes": [],
            "acceptanceCriteria": [{
              "scenario": "Add todo",
              "given": [],
              "when": [],
              "then": [],
              "verificationKinds": []
            }]
          }],
          "qualityAttributes": [],
          "assumptions": [],
          "inferredDefaults": [],
          "productAmbiguities": []
        }
        """;

    private static readonly string InvalidDomainJson = ValidDomainJson.Replace(
        "\"title\": \"Todo planner\"", "\"title\": \"\"");

    [Fact]
    public async Task Retry_loop_recovers_on_second_attempt_with_validation_feedback()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = await RetryHarness.CreateAsync(
            [InvalidDomainJson, ValidDomainJson], maxIterations: 3, ct);
        try
        {
            var output = await harness.Engine.WaitForCompletionAsync<JsonElement>(
                harness.RunId, cancellationToken: ct);
            harness.Envelope = output.Clone();
            harness.Router.RequestCalls.Should().Be(2);

            // The validation failure was fed back into the retry prompt.
            var secondUserText = string.Concat(harness.Requests[1].Messages
                .First(m => m.Role == "user").Parts
                .OfType<LlmTextContent>()
                .Select(part => part.Text));
            secondUserText.Should().Contain("has no title or summary");

            var envelope = harness.Envelope;
            envelope.GetProperty("ok").GetBoolean().Should().BeTrue();
            envelope.GetProperty("domain").GetProperty("title").GetString()
                .Should().Be("Todo planner");

            var callsAfterFirst = harness.Router.RequestCalls;
            await harness.Engine.ExecuteAsync(harness.RunId, ct);
            harness.Router.RequestCalls.Should().Be(callsAfterFirst);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retry_loop_gives_up_after_max_iterations_with_loop_limit_exceeded()
    {
        // Exhaustion surfaces as the runtime's LoopLimitExceeded contract
        // failure (the service equivalent is returning the last stage
        // failure); the bounded budget itself is enforced, with exactly
        // maxIterations provider calls and no further attempts.
        var ct = TestContext.Current.CancellationToken;
        var harness = await RetryHarness.CreateAsync(
            [InvalidDomainJson, InvalidDomainJson, InvalidDomainJson, InvalidDomainJson],
            maxIterations: 3, ct);
        try
        {
            var act = () => harness.Engine.WaitForCompletionAsync<JsonElement>(
                harness.RunId, cancellationToken: ct);
            (await act.Should().ThrowAsync<WorkflowExecutionFailedException>())
                .WithMessage("*exceeded its maximum of 3 iterations*");
            harness.Router.RequestCalls.Should().Be(3);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    private sealed class RetryHarness
    {
        public QueuedDomainRouter Router { get; }
        public List<LlmRequest> Requests => Router.Requests;
        public WorkflowEngine Engine { get; }
        public Guid RunId { get; }
        public JsonElement Envelope { get; set; }
        private readonly string root;

        private RetryHarness(
            QueuedDomainRouter router,
            WorkflowEngine engine,
            Guid runId,
            string root)
        {
            Router = router;
            Engine = engine;
            RunId = runId;
            this.root = root;
        }

        public static async Task<RetryHarness> CreateAsync(
            IReadOnlyList<string> scriptedResponses, int maxIterations, CancellationToken ct)
        {
            var promptsRoot = FindPromptsRoot();
            var systemBytes = await File.ReadAllBytesAsync(
                Path.Combine(promptsRoot, "domain-discovery", "system.sbn"), ct);
            var userBytes = await File.ReadAllBytesAsync(
                Path.Combine(promptsRoot, "domain-discovery", "user.sbn"), ct);

            var templateEngine = new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot));
            var promptBuilder = new DomainDiscoveryPromptBuilder(templateEngine);
            var repairer = new LlmStructuredOutputRepairer(JsonRepairPipeline.Create());

            var contextDescriptor = PlanningFuwenDescriptors.Context();
            var profile = PlanningFuwenDescriptors.Profile(Model, MaxTokens);
            var template = PlanningFuwenDescriptors.Template(systemBytes, userBytes);
            var (schemaDescriptor, attemptSchema) = PlanningFuwenDescriptors.AttemptSchema();
            var attemptType = new NamedTypeReference(schemaDescriptor);
            var str = new PrimitiveType(FuwenPrimitiveKind.String);
            var optionalStr = new OptionalType(str);

            var loopPath = StructuralNodeIdentity.Create("realRetry", "attempts");
            var answerPath = loopPath + "/$body/answer";
            var returnPath = StructuralNodeIdentity.Create("realRetry", "return_result");
            // R24: the envelope seed is a plain object literal against the
            // named attempt schema (previously required field-wise ObjectBinding).
            using var initial = JsonDocument.Parse("""{"ok":false,"domain":null,"error":""}""");
            using var breakLiteral = JsonDocument.Parse("true");
            var plan = new WorkflowPlanBuilder("realRetry", "1", str, attemptType, "routing/1")
                .AddSchema(attemptSchema)
                .AddNode(new RepeatNode(
                    "attempts", loopPath, maxIterations, attemptType,
                    new LiteralBinding(initial.RootElement.Clone()),
                    [
                        new InferenceNode("answer", answerPath, profile, template,
                            [
                                new ArgumentBinding("request", new InputBinding([])),
                                new ArgumentBinding("previousFailure", new LoopStateBinding(["error"])),
                            ], [], attemptType, []),
                    ],
                    new NodeOutputBinding(answerPath, []),
                    new ConditionExpression(ConditionOperator.Equal,
                        new NodeOutputBinding(answerPath, ["ok"]),
                        new LiteralBinding(breakLiteral.RootElement.Clone())),
                    attemptType))
                .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(loopPath, [])))
                .SetExecutionOrder(new WorkflowExecutionOrder([
                    new WorkflowExecutionRegion("realRetry", [
                        new WorkflowExecutionPhase([loopPath]),
                        new WorkflowExecutionPhase([returnPath]),
                    ]),
                    new WorkflowExecutionRegion("realRetry/attempts/$body", [
                        new WorkflowExecutionPhase([answerPath]),
                    ]),
                ]))
                .BuildV6();

            var catalogue = new InMemoryTrustedCatalogue([
                new TrustedCatalogueDescriptor(contextDescriptor, callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("request", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                new TrustedCatalogueDescriptor(profile, callableContract: new CallableContract(
                    new CallableSignature(
                        [new CallableParameter("request", str), new CallableParameter("previousFailure", optionalStr)],
                        attemptType),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                new TrustedCatalogueDescriptor(template),
                new TrustedCatalogueDescriptor(schemaDescriptor, schemaDefinition: attemptSchema),
            ]);
            var admission = await new WorkflowAdmissionService(
                    new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
                .AdmitAsync(plan, cancellationToken: ct);
            admission.Succeeded.Should().BeTrue(
                string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));

            var router = new QueuedDomainRouter(scriptedResponses);
            var executor = new PlanningDomainDiscoveryExecutor(
                router, promptBuilder, repairer, Model, MaxTokens, outputEnvelope: true);
            var registration = await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    new FuwenZhinuProviderRuntimeIdentity(
                        admission.Receipt!.CatalogueSnapshotRevision,
                        admission.Receipt.ResolvedDescriptorSetFingerprint),
                    new FuwenZhinuExecutionPorts(
                        new UnusedActivity(), new UnusedContext(), executor))
                .CreateAsync("fuwen.real-retry", "1", admission, ct);
            var root = Path.Combine(Path.GetTempPath(), "guyabano-real-retry", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"Plan a todo app.\"");
            var runId = await engine.StartAsync("fuwen.real-retry", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            return new RetryHarness(router, engine, runId, root);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            for (var i = 0; i < 5 && Directory.Exists(root); i++)
            {
                try { Directory.Delete(root, true); break; } catch { await Task.Delay(50 * (i + 1)); }
            }
        }

        private static string FindPromptsRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "prompts", "domain-discovery", "system.sbn");
                if (File.Exists(candidate))
                    return Path.Combine(directory.FullName, "prompts");
            }
            throw new DirectoryNotFoundException("Could not locate the Guyabano prompts root.");
        }
    }

    private sealed class UnusedActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class QueuedDomainRouter(IReadOnlyList<string> scriptedResponses) : ILlmRouter
    {
        private int next;
        public List<LlmRequest> Requests { get; } = [];
        public int RequestCalls { get; private set; }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            RequestCalls++;
            Requests.Add(request);
            var index = Math.Min(next, scriptedResponses.Count - 1);
            next++;
            return StreamSingle(scriptedResponses[index]);
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
