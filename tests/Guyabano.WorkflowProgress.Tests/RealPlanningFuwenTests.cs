#pragma warning disable xUnit1030
using System.Text.Json;
using Penghou.Guihua.Baize;
using Penghou.Guihua;
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
/// Slice 1 of real Guyabano behavior in Fuwen: the domain-discovery planning
/// stage runs through a Fuwen inference node using the real prompt pack,
/// model, schema, repairer, parser, and validator — not stubs. The test
/// differentially compares one direct stage attempt against the same attempt
/// executed durably through Fuwen/Zhinu.
/// </summary>
public sealed class RealPlanningFuwenTests
{
    private const string Model = "deepseek-v4-flash";
    private const int MaxTokens = 8000;

    private const string CannedDomainJson = """
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

    [Fact]
    public async Task Domain_discovery_through_fuwen_matches_direct_stage_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var promptsRoot = FindPromptsRoot();
        var systemBytes = await File.ReadAllBytesAsync(
            Path.Combine(promptsRoot, "domain-discovery", "system.sbn"), ct);
        var userBytes = await File.ReadAllBytesAsync(
            Path.Combine(promptsRoot, "domain-discovery", "user.sbn"), ct);

        var templateEngine = new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot));
        var promptBuilder = new DomainDiscoveryPromptBuilder(templateEngine);
        var repairer = new LlmStructuredOutputRepairer(JsonRepairPipeline.Create());
        var format = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<DomainDiscovery>());

        // Path A: direct single stage attempt (mirrors ExecuteStageAsync).
        var directRouter = new CannedDomainRouter(CannedDomainJson);
        var directRequest = await promptBuilder.BuildAsync(
            new DomainDiscoveryPromptContext("Plan a todo app.", format, MaxTokens, null), ct);
        var directResponse = await directRouter.CompleteStreamingAsync(Model, directRequest, ct);
        var directRepaired = await repairer.RepairAsync(directResponse, format, ct);
        var directParsed = StructuredPlanningStageParser<DomainDiscovery>.Parse(directRepaired);
        directParsed.Succeeded.Should().BeTrue(directParsed.Error);
        directParsed.Value.Should().NotBeNull();
        StagedPlanningValidator.ValidateDomain(directParsed.Value!)
            .Should().BeEmpty();
        var expectedJson = JsonSerializer.Serialize(directParsed.Value);

        // Path B: same attempt through Fuwen/Zhinu with real descriptors.
        var contextDescriptor = PlanningFuwenDescriptors.Context();
        var profile = PlanningFuwenDescriptors.Profile(Model, MaxTokens);
        var template = PlanningFuwenDescriptors.Template("domain-discovery", "Guyabano.CodeGeneration.Planning.DomainDiscovery", systemBytes, userBytes);
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var ctxPath = StructuralNodeIdentity.Create("realPlanning", "ctx");
        var inferPath = StructuralNodeIdentity.Create("realPlanning", "domain");
        var returnPath = StructuralNodeIdentity.Create("realPlanning", "return_result");
        var plan = new WorkflowPlanBuilder("realPlanning", "1", str, new PrimitiveType(FuwenPrimitiveKind.Json), "routing/1")
            .AddNode(new ContextNode("ctx", ctxPath, contextDescriptor,
                [new ArgumentBinding("request", new InputBinding([]))], str))
            // R25: the OptionalType previousFailure parameter is omitted
            // (previously required an explicit null literal).
            .AddNode(new InferenceNode("domain", inferPath, profile, template,
                [
                    new ArgumentBinding("request", new NodeOutputBinding(ctxPath, [])),
                ], [], new PrimitiveType(FuwenPrimitiveKind.Json),
                [new ContextRequirement("ctx", new NodeOutputBinding(ctxPath, []), str)]))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("realPlanning", [
                    new WorkflowExecutionPhase([ctxPath]),
                    new WorkflowExecutionPhase([inferPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .BuildV3();

        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(contextDescriptor, callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("request", str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(profile, callableContract: new CallableContract(
                new CallableSignature(
                    [new CallableParameter("request", str), new CallableParameter("previousFailure", new OptionalType(str))],
                    new PrimitiveType(FuwenPrimitiveKind.Json)),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(template),
        ]);
        var admission = await new WorkflowAdmissionService(
                new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));

        var fuwenRouter = new CannedDomainRouter(CannedDomainJson);
        var executor = new PlanningDomainDiscoveryExecutor(
            fuwenRouter, promptBuilder, repairer, Model, MaxTokens);
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(), new EchoPlanningContext(), executor))
            .CreateAsync("fuwen.real-planning", "1", admission, ct);
        var root = Path.Combine(Path.GetTempPath(), "guyabano-real-planning", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"Plan a todo app.\"");
            var runId = await engine.StartAsync("fuwen.real-planning", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            // Same structured result through both paths (compared
            // semantically: Fuwen canonicalizes key order on the wire).
            var fuwenDomain = JsonSerializer.Deserialize<DomainDiscovery>(output.GetRawText());
            var directDomain = JsonSerializer.Deserialize<DomainDiscovery>(expectedJson);
            fuwenDomain.Should().NotBeNull();
            fuwenDomain.Should().BeEquivalentTo(directDomain);

            // Same real prompts rendered in both paths.
            RenderedPrompts(directRouter.Requests.Single()).Should().Be(
                RenderedPrompts(fuwenRouter.Requests.Single()));

            // The pack is the real domain-discovery template, not a stub:
            // rendering it directly embeds the request text.
            var userTemplate = await templateEngine.RenderAsync(
                "domain-discovery/user.sbn", new { Request = "Plan a todo app.", PreviousFailure = (string?)null }, ct);
            userTemplate.Should().Contain("Plan a todo app.");

            var callsAfterFirst = fuwenRouter.RequestCalls;
            await engine.ExecuteAsync(runId, ct);
            fuwenRouter.RequestCalls.Should().Be(callsAfterFirst);
        }
        finally
        {
            for (var i = 0; i < 5 && Directory.Exists(root); i++)
            {
                try { Directory.Delete(root, true); break; } catch { Thread.Sleep(50 * (i + 1)); }
            }
        }
    }

    private static string RenderedPrompts(LlmRequest request) =>
        JsonSerializer.Serialize(request.Messages.Select(message => new
        {
            role = message.Role,
            content = message.Parts,
        }));

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

    private sealed class EchoPlanningContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken ct = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input));
            var snap = new ContextSnapshotReference(request.Provider, "snap-real",
                new ContentDigest("sha256", "request/v1", new string('c', 64)),
                new ContentDigest("sha256", "content/v1", new string('d', 64)), [],
                "policy/1", new ContextSnapshotBudgetEvidence(false, null, null, null, null), DateTimeOffset.UtcNow);
            return ValueTask.FromResult(ContextExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement), snap));
        }
    }

    private sealed class UnusedActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class CannedDomainRouter(string cannedJson) : ILlmRouter
    {
        public List<LlmRequest> Requests { get; } = [];
        public int RequestCalls { get; private set; }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            RequestCalls++;
            Requests.Add(request);
            return StreamSingle(cannedJson);
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
