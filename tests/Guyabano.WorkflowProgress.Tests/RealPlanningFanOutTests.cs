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
/// Slice 3 of real Guyabano behavior in Fuwen: per-context contract and
/// component design run as a keyed fan-out over the admitted topology, with
/// the real prompt packs, schemas, repairers, parsers, and validators in
/// every stage. Independent contexts only (no inter-context dependencies);
/// dependent chains need sequential handling (slice 4).
/// </summary>
public sealed class RealPlanningFanOutTests
{
    private const string Model = "deepseek-v4-flash";

    private const string DomainJson = """
        {
          "mission": {
            "guidingIntent": "Let users manage todos and notes.",
            "successOutcomes": ["Todos and notes can be added."],
            "constraints": [],
            "nonGoals": []
          },
          "title": "TodoNotes planner",
          "summary": "Plan a minimal todos and notes application.",
          "terms": [{ "name": "Todo", "definition": "A single trackable task." }],
          "capabilities": [
            { "name": "ManageTodos", "description": "CRUD for todos.", "businessRules": [] },
            { "name": "ManageNotes", "description": "CRUD for notes.", "businessRules": [] }
          ],
          "useCases": [
            {
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
            },
            {
              "name": "AddNote",
              "capabilityName": "ManageNotes",
              "actor": "User",
              "objective": "Add a note.",
              "preconditions": [],
              "inputs": [],
              "businessRules": [],
              "outcomes": ["Note is stored."],
              "errorOutcomes": [],
              "acceptanceCriteria": [{
                "scenario": "Add note",
                "given": [],
                "when": [],
                "then": [],
                "verificationKinds": []
              }]
            }
          ],
          "qualityAttributes": [],
          "assumptions": [],
          "inferredDefaults": [],
          "productAmbiguities": []
        }
        """;

    private const string TopologyJson = """
        {
          "solution": { "name": "TodoNotes", "path": "TodoNotes.slnx" },
          "projects": [{
            "name": "App",
            "path": "src/App/App.csproj",
            "kind": "Library",
            "role": "Application",
            "targetFramework": "net10.0",
            "responsibilities": ["Host tasks."],
            "projectDependencies": [],
            "packages": []
          }],
          "boundedContexts": [
            {
              "name": "Todos",
              "purpose": "Own todo management.",
              "capabilityNames": ["ManageTodos"],
              "dependsOnContextNames": [],
              "inboundAdapters": [],
              "outboundAdapters": []
            },
            {
              "name": "Notes",
              "purpose": "Own note management.",
              "capabilityNames": ["ManageNotes"],
              "dependsOnContextNames": [],
              "inboundAdapters": [],
              "outboundAdapters": []
            }
          ],
          "modules": [
            { "name": "M1", "boundedContextName": "Todos", "projectName": "App", "responsibilities": [] },
            { "name": "M2", "boundedContextName": "Notes", "projectName": "App", "responsibilities": [] }
          ],
          "decisions": []
        }
        """;

    private const string TodosCatalogJson = """
        {
          "boundedContextName": "Todos",
          "contracts": [{
            "name": "TodoContracts",
            "kind": "Contracts",
            "moduleName": "M1",
            "purpose": "Todo DTOs.",
            "members": [],
            "capabilityNames": ["ManageTodos"]
          }],
          "decisions": [],
          "inferredDefaults": []
        }
        """;

    private const string NotesCatalogJson = """
        {
          "boundedContextName": "Notes",
          "contracts": [{
            "name": "NoteContracts",
            "kind": "Contracts",
            "moduleName": "M2",
            "purpose": "Note DTOs.",
            "members": [],
            "capabilityNames": ["ManageNotes"]
          }],
          "decisions": [],
          "inferredDefaults": []
        }
        """;

    private const string TodosManifestJson = """
        {
          "boundedContextName": "Todos",
          "components": [{
            "name": "TodoService",
            "kind": "Service",
            "moduleName": "M1",
            "projectName": "App",
            "files": ["TodoService.cs"],
            "responsibilities": [],
            "definesContractNames": ["TodoContracts"],
            "implementsPortNames": [],
            "consumesContractNames": [],
            "usesConcreteComponentNames": [],
            "registersImplementationNames": [],
            "testsComponentNames": [],
            "capabilityNames": ["ManageTodos"],
            "acceptanceCriterionIds": ["AC-ADDTODO-ADD-TODO"],
            "lifetime": "Singleton",
            "complexityPoints": 2,
            "verificationKinds": ["Compilation"]
          }],
          "decisions": [],
          "inferredDefaults": []
        }
        """;

    private const string NotesManifestJson = """
        {
          "boundedContextName": "Notes",
          "components": [{
            "name": "NoteService",
            "kind": "Service",
            "moduleName": "M2",
            "projectName": "App",
            "files": ["NoteService.cs"],
            "responsibilities": [],
            "definesContractNames": ["NoteContracts"],
            "implementsPortNames": [],
            "consumesContractNames": [],
            "usesConcreteComponentNames": [],
            "registersImplementationNames": [],
            "testsComponentNames": [],
            "capabilityNames": ["ManageNotes"],
            "acceptanceCriterionIds": ["AC-ADDNOTE-ADD-NOTE"],
            "lifetime": "Singleton",
            "complexityPoints": 2,
            "verificationKinds": ["Compilation"]
          }],
          "decisions": [],
          "inferredDefaults": []
        }
        """;

    [Fact]
    public async Task Per_context_contract_and_component_design_fans_out_with_real_prompts()
    {
        var ct = TestContext.Current.CancellationToken;
        var promptsRoot = FindPromptsRoot();
        static async Task<byte[]> PackBytes(string root, string pack, string file, CancellationToken ct) =>
            await File.ReadAllBytesAsync(Path.Combine(root, pack, file), ct);

        var templateEngine = new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot));
        var domainBuilder = new DomainDiscoveryPromptBuilder(templateEngine);
        var topologyBuilder = new SolutionTopologyPromptBuilder(templateEngine);
        var contractBuilder = new ContractDesignPromptBuilder(templateEngine);
        var componentBuilder = new ComponentDesignPromptBuilder(templateEngine);
        var repairer = new LlmStructuredOutputRepairer(JsonRepairPipeline.Create());

        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        var list8 = new ListType(json, 8);
        var (bundleSchemaDescriptor, bundleSchema) = PlanningFuwenDescriptors.BundleSchema();
        var bundleType = new NamedTypeReference(bundleSchemaDescriptor);
        var bundleList = new ListType(bundleType, 8);

        var contextDescriptor = PlanningFuwenDescriptors.Context();
        var domainProfile = PlanningFuwenDescriptors.StageProfile("domain-discovery", Model, 8000);
        var topologyProfile = PlanningFuwenDescriptors.StageProfile("solution-topology", Model, 10000);
        var contractProfile = PlanningFuwenDescriptors.StageProfile("contract-design", Model, 12000);
        var componentProfile = PlanningFuwenDescriptors.StageProfile("component-design", Model, 16000);
        var bundleActivity = PlanningFuwenDescriptors.BundleActivity();
        var domainTemplate = PlanningFuwenDescriptors.Template(
            "domain-discovery", "Guyabano.CodeGeneration.Planning.DomainDiscovery",
            await PackBytes(promptsRoot, "domain-discovery", "system.sbn", ct),
            await PackBytes(promptsRoot, "domain-discovery", "user.sbn", ct));
        var topologyTemplate = PlanningFuwenDescriptors.Template(
            "solution-topology", "Guyabano.CodeGeneration.Planning.SolutionTopology",
            await PackBytes(promptsRoot, "solution-topology", "system.sbn", ct),
            await PackBytes(promptsRoot, "solution-topology", "user.sbn", ct));
        var contractTemplate = PlanningFuwenDescriptors.Template(
            "contract-design", "Guyabano.CodeGeneration.Planning.BoundedContextContractCatalog",
            await PackBytes(promptsRoot, "contract-design", "system.sbn", ct),
            await PackBytes(promptsRoot, "contract-design", "user.sbn", ct));
        var componentTemplate = PlanningFuwenDescriptors.Template(
            "component-design", "Guyabano.CodeGeneration.Planning.BoundedContextComponentManifest",
            await PackBytes(promptsRoot, "component-design", "system.sbn", ct),
            await PackBytes(promptsRoot, "component-design", "user.sbn", ct));

        var ctxPath = StructuralNodeIdentity.Create("realFanOut", "ctx");
        var domainPath = StructuralNodeIdentity.Create("realFanOut", "domain");
        var topologyPath = StructuralNodeIdentity.Create("realFanOut", "topology");
        var bundlePath = StructuralNodeIdentity.Create("realFanOut", "bundles");
        var fanOutPath = StructuralNodeIdentity.Create("realFanOut", "design");
        var contractPath = fanOutPath + "/$body/contracts";
        var manifestPath = fanOutPath + "/$body/manifest";
        var returnPath = StructuralNodeIdentity.Create("realFanOut", "return_result");

        var plan = new WorkflowPlanBuilder("realFanOut", "1", str, list8, "routing/1")
            .AddSchema(bundleSchema)
            .AddNode(new ContextNode("ctx", ctxPath, contextDescriptor,
                [new ArgumentBinding("request", new InputBinding([]))], str))
            .AddNode(new InferenceNode("domain", domainPath, domainProfile, domainTemplate,
                [new ArgumentBinding("request", new NodeOutputBinding(ctxPath, []))], [], json, []))
            .AddNode(new InferenceNode("topology", topologyPath, topologyProfile, topologyTemplate,
                [
                    new ArgumentBinding("request", new NodeOutputBinding(ctxPath, [])),
                    new ArgumentBinding("domain", new NodeOutputBinding(domainPath, [])),
                ], [], json, []))
            .AddNode(new ActivityNode("bundles", bundlePath, bundleActivity,
                [
                    new ArgumentBinding("topology", new NodeOutputBinding(topologyPath, [])),
                    new ArgumentBinding("domain", new NodeOutputBinding(domainPath, [])),
                ], bundleList))
            .AddFanOut(new FanOutNode(
                "design", fanOutPath,
                new NodeOutputBinding(bundlePath, []),
                new FanOutItemBinding("bundle", bundleType),
                new FanOutItemValueBinding(["name"]),
                [
                    new InferenceNode("contracts", contractPath, contractProfile, contractTemplate,
                        [new ArgumentBinding("bundle", new FanOutItemValueBinding(["bundle"]))], [], json, []),
                    new InferenceNode("manifest", manifestPath, componentProfile, componentTemplate,
                        [
                            new ArgumentBinding("bundle", new FanOutItemValueBinding(["bundle"])),
                            new ArgumentBinding("catalog", new NodeOutputBinding(contractPath, [])),
                        ], [], json, []),
                ],
                new NodeOutputBinding(manifestPath, []),
                list8,
                8, 2))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(fanOutPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("realFanOut", [
                    new WorkflowExecutionPhase([ctxPath]),
                    new WorkflowExecutionPhase([domainPath]),
                    new WorkflowExecutionPhase([topologyPath]),
                    new WorkflowExecutionPhase([bundlePath]),
                    new WorkflowExecutionPhase([fanOutPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion("realFanOut/design/$body", [
                    new WorkflowExecutionPhase([contractPath]),
                    new WorkflowExecutionPhase([manifestPath]),
                ]),
            ]))
            .BuildV4();

        CallableContract StageContract(params (string Name, FuwenType Type)[] parameters) => new(
            new CallableSignature([.. parameters.Select(p => new CallableParameter(p.Name, p.Type))], json),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(contextDescriptor, callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("request", str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(domainProfile, callableContract:
                StageContract(("request", str))),
            new TrustedCatalogueDescriptor(topologyProfile, callableContract:
                StageContract(("request", str), ("domain", json))),
            new TrustedCatalogueDescriptor(contractProfile, callableContract:
                StageContract(("bundle", json))),
            new TrustedCatalogueDescriptor(componentProfile, callableContract:
                StageContract(("bundle", json), ("catalog", json))),
            new TrustedCatalogueDescriptor(domainTemplate),
            new TrustedCatalogueDescriptor(topologyTemplate),
            new TrustedCatalogueDescriptor(contractTemplate),
            new TrustedCatalogueDescriptor(componentTemplate),
            new TrustedCatalogueDescriptor(bundleActivity, callableContract: new CallableContract(
                new CallableSignature(
                    [new CallableParameter("topology", json), new CallableParameter("domain", json)], bundleList),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(bundleSchemaDescriptor, schemaDefinition: bundleSchema),
        ]);
        var admission = await new WorkflowAdmissionService(
                new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path} expected={d.Expected} actual={d.Actual}")));

        var router = new KeyedStageRouter();
        var domainExecutor = new PlanningDomainDiscoveryExecutor(
            router, domainBuilder, repairer, Model, 8000);
        var topologyExecutor = new PlanningTopologyExecutor(
            router, topologyBuilder, repairer, Model, 10000);
        var contractExecutor = new PlanningContractExecutor(
            router, contractBuilder, repairer, Model, 12000);
        var componentExecutor = new PlanningComponentExecutor(
            router, componentBuilder, repairer, Model, 16000);
        var ports = new FuwenZhinuExecutionPorts(
            new BundleContractInputsActivity(),
            new EchoPlanningContext(),
            new StageInferenceRouter(domainExecutor, topologyExecutor, contractExecutor, componentExecutor));
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                ports)
            .CreateAsync("fuwen.real-fanout", "1", admission, ct);
        var root = Path.Combine(Path.GetTempPath(), "guyabano-real-fanout", Guid.NewGuid().ToString("N"));
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

            using var input = JsonDocument.Parse("\"Plan todos and notes.\"");
            var runId = await engine.StartAsync("fuwen.real-fanout", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            // Source order preserved: Todos first, Notes second.
            var manifests = output.EnumerateArray().ToArray();
            manifests.Should().HaveCount(2);
            manifests[0].GetProperty("boundedContextName").GetString().Should().Be("Todos");
            manifests[1].GetProperty("boundedContextName").GetString().Should().Be("Notes");
            manifests[0].GetProperty("components").EnumerateArray().Single()
                .GetProperty("definesContractNames").EnumerateArray().Single()
                .GetString().Should().Be("TodoContracts");
            manifests[1].GetProperty("components").EnumerateArray().Single()
                .GetProperty("definesContractNames").EnumerateArray().Single()
                .GetString().Should().Be("NoteContracts");

            // Every stage ran through its real prompt pack exactly as often
            // as the pipeline demands: 1 domain, 1 topology, 2 contracts,
            // 2 manifests.
            router.RequestCalls.Should().Be(6);
            var markers = router.Requests.Select(RequestMarker).ToArray();
            markers.Should().ContainSingle(m => m == "domain");
            markers.Should().ContainSingle(m => m == "topology");
            markers.Where(m => m == "contract-Todos").Should().HaveCount(1);
            markers.Where(m => m == "contract-Notes").Should().HaveCount(1);
            markers.Where(m => m == "component-Todos").Should().HaveCount(1);
            markers.Where(m => m == "component-Notes").Should().HaveCount(1);

            var callsAfterFirst = router.RequestCalls;
            await engine.ExecuteAsync(runId, ct);
            router.RequestCalls.Should().Be(callsAfterFirst);
        }
        finally
        {
            for (var i = 0; i < 5 && Directory.Exists(root); i++)
            {
                try { Directory.Delete(root, true); break; } catch { Thread.Sleep(50 * (i + 1)); }
            }
        }
    }

    private static string RequestMarker(LlmRequest request)
    {
        var text = string.Concat(request.Messages
            .SelectMany(m => m.Parts)
            .OfType<LlmTextContent>()
            .Select(p => p.Text));
        if (text.Contains("Create the solution topology", StringComparison.Ordinal))
            return "topology";
        if (text.Contains("Design components for this bounded context:", StringComparison.Ordinal))
            return "component-" + ContextAfter(text, "Design components for this bounded context:");
        if (text.Contains("Design contracts for this bounded context:", StringComparison.Ordinal))
            return "contract-" + ContextAfter(text, "Design contracts for this bounded context:");
        return "domain";
    }

    private static string ContextAfter(string text, string marker)
    {
        var tail = text[(text.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
        var match = System.Text.RegularExpressions.Regex.Match(
            tail, "\"name\"\\s*:\\s*\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : "unknown";
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

    private sealed class EchoPlanningContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken ct = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input));
            var snap = new ContextSnapshotReference(request.Provider, "snap-fanout",
                new ContentDigest("sha256", "request/v1", new string('c', 64)),
                new ContentDigest("sha256", "content/v1", new string('d', 64)), [],
                "policy/1", new ContextSnapshotBudgetEvidence(false, null, null, null, null), DateTimeOffset.UtcNow);
            return ValueTask.FromResult(ContextExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement), snap));
        }
    }

    private sealed class StageInferenceRouter(
        PlanningDomainDiscoveryExecutor domain,
        PlanningTopologyExecutor topology,
        PlanningContractExecutor contract,
        PlanningComponentExecutor component) : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken ct = default)
        {
            var template = request.PromptTemplate.Name switch
            {
                var name when name == PlanningFuwenDescriptors.TemplateName("domain-discovery") => (IInferenceExecutor)domain,
                var name when name == PlanningFuwenDescriptors.TemplateName("solution-topology") => topology,
                var name when name == PlanningFuwenDescriptors.TemplateName("contract-design") => contract,
                var name when name == PlanningFuwenDescriptors.TemplateName("component-design") => component,
                _ => throw new InvalidOperationException($"Unknown prompt template '{request.PromptTemplate.Name}'."),
            };
            return template.ExecuteAsync(request, ct);
        }
    }

    private sealed class KeyedStageRouter : ILlmRouter
    {
        public List<LlmRequest> Requests { get; } = [];
        public int RequestCalls { get; private set; }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            RequestCalls++;
            Requests.Add(request);
            return StreamSingle(ResponseFor(request));
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

        private static string ResponseFor(LlmRequest request) =>
            RequestMarker(request) switch
            {
                "domain" => DomainJson,
                "topology" => TopologyJson,
                "contract-Todos" => TodosCatalogJson,
                "contract-Notes" => NotesCatalogJson,
                "component-Todos" => TodosManifestJson,
                "component-Notes" => NotesManifestJson,
                _ => throw new InvalidOperationException("Unrecognized planning stage request."),
            };

        private static async IAsyncEnumerable<LlmStreamEvent> StreamSingle(
            string delta,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new LlmStreamEvent(delta, null, "stop", null, null, null, null, null, null);
        }
    }
}
