using System.Text.Json;
using Guyabano.CodeGeneration.Planning.Extensions;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Guyabano.Llm.Prompting;
using Guyabano.Llm.Prompting.Extensions;
using Penghou.Baize.Claude;
using Penghou.Baize.Gemini;
using Penghou.Baize.Ollama;
using Penghou.Baize.OpenAi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Penghou.Baize.Router.Extensions;
using Penghou.Baize.Tools.Extensions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

// Live Fuwen-hosted planning run: context -> domain discovery ->
// solution topology -> per-context contract/component fan-out, using the
// same appsettings.json (CodeGeneration + LlmRouting) as the worker.
// Usage: Guyabano.FuwenPlanning "Plan a todo app." [--workdir <dir>]
var request = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
var workdirFlag = Array.FindIndex(
    args, arg => string.Equals(arg, "--workdir", StringComparison.Ordinal));
var workdir = workdirFlag >= 0 && workdirFlag + 1 < args.Length
    ? args[workdirFlag + 1]
    : Path.Combine(Path.GetTempPath(), "guyabano-fuwen-live", Guid.NewGuid().ToString("N"));
if (string.IsNullOrWhiteSpace(request))
{
    Console.Error.WriteLine("Usage: Guyabano.FuwenPlanning \"<request>\" [--workdir <dir>]");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient("llm", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: false,
    reloadOnChange: false);
builder.Services.AddOpenAiLlmProvider();
builder.Services.AddClaudeLlmProvider();
builder.Services.AddGeminiLlmProvider();
builder.Services.AddOllamaLlmProvider();
builder.Services.AddLlmRouting(builder.Configuration);
builder.Services.AddLlmTools();
builder.Services.AddLlmPrompting(Path.Combine(AppContext.BaseDirectory, "prompts"));
builder.Services.AddCodeGenerationPlanning();
builder.Services.AddFuwenPlanning(builder.Configuration);
using var host = builder.Build();
await host.StartAsync(cts.Token);
var services = host.Services;

var options = services.GetRequiredService<IOptions<FuwenPlanningOptions>>().Value;
var promptsRoot = Path.Combine(AppContext.BaseDirectory, "prompts");
static async Task<byte[]> PackBytes(string root, string pack, string file, CancellationToken ct) =>
    await File.ReadAllBytesAsync(Path.Combine(root, pack, file), ct);

var str = new PrimitiveType(FuwenPrimitiveKind.String);
var json = new PrimitiveType(FuwenPrimitiveKind.Json);
var list8 = new ListType(json, 8);
var contextDescriptor = PlanningFuwenDescriptors.Context();
var domainProfile = PlanningFuwenDescriptors.StageProfile("domain-discovery", options.PlannerModel, options.DomainMaxTokens);
var topologyProfile = PlanningFuwenDescriptors.StageProfile("solution-topology", options.PlannerModel, options.TopologyMaxTokens);
var contractProfile = PlanningFuwenDescriptors.StageProfile("contract-design", options.PlannerModel, options.ContractMaxTokens);
var componentProfile = PlanningFuwenDescriptors.StageProfile("component-design", options.PlannerModel, options.ComponentMaxTokens);
var bundleActivity = PlanningFuwenDescriptors.BundleActivity();
var (bundleSchemaDescriptor, bundleSchema) = PlanningFuwenDescriptors.BundleSchema();
var bundleType = new NamedTypeReference(bundleSchemaDescriptor);
var bundleList = new ListType(bundleType, 8);
async Task<DescriptorReference> Template(string pack, string schema)
{
    return PlanningFuwenDescriptors.Template(
        pack, schema,
        await PackBytes(promptsRoot, pack, "system.sbn", cts.Token),
        await PackBytes(promptsRoot, pack, "user.sbn", cts.Token));
}
var domainTemplate = await Template("domain-discovery", "Guyabano.CodeGeneration.Planning.DomainDiscovery");
var topologyTemplate = await Template("solution-topology", "Guyabano.CodeGeneration.Planning.SolutionTopology");
var contractTemplate = await Template("contract-design", "Guyabano.CodeGeneration.Planning.BoundedContextContractCatalog");
var componentTemplate = await Template("component-design", "Guyabano.CodeGeneration.Planning.BoundedContextComponentManifest");

var ctxPath = StructuralNodeIdentity.Create("livePlanning", "ctx");
var domainPath = StructuralNodeIdentity.Create("livePlanning", "domain");
var topologyPath = StructuralNodeIdentity.Create("livePlanning", "topology");
var bundlePath = StructuralNodeIdentity.Create("livePlanning", "bundles");
var fanOutPath = StructuralNodeIdentity.Create("livePlanning", "design");
var contractPath = fanOutPath + "/$body/contracts";
var manifestPath = fanOutPath + "/$body/manifest";
var returnPath = StructuralNodeIdentity.Create("livePlanning", "return_result");

var plan = new WorkflowPlanBuilder("livePlanning", "1", str, list8, "routing/1")
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
        8, 4))
    .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(fanOutPath, [])))
    .SetExecutionOrder(new WorkflowExecutionOrder([
        new WorkflowExecutionRegion("livePlanning", [
            new WorkflowExecutionPhase([ctxPath]),
            new WorkflowExecutionPhase([domainPath]),
            new WorkflowExecutionPhase([topologyPath]),
            new WorkflowExecutionPhase([bundlePath]),
            new WorkflowExecutionPhase([fanOutPath]),
            new WorkflowExecutionPhase([returnPath]),
        ]),
        new WorkflowExecutionRegion("livePlanning/design/$body", [
            new WorkflowExecutionPhase([contractPath]),
            new WorkflowExecutionPhase([manifestPath]),
        ]),
    ]))
    .BuildV4();

static CallableContract StageContract(params (string Name, FuwenType Type)[] parameters) => new(
    new CallableSignature([.. parameters.Select(p => new CallableParameter(p.Name, p.Type))], new PrimitiveType(FuwenPrimitiveKind.Json)),
    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);
var catalogue = new InMemoryTrustedCatalogue([
    new TrustedCatalogueDescriptor(contextDescriptor, callableContract: new CallableContract(
        new CallableSignature([new CallableParameter("request", str)], str),
        CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    new TrustedCatalogueDescriptor(domainProfile, callableContract: StageContract(("request", str))),
    new TrustedCatalogueDescriptor(topologyProfile, callableContract: StageContract(("request", str), ("domain", json))),
    new TrustedCatalogueDescriptor(contractProfile, callableContract: StageContract(("bundle", json))),
    new TrustedCatalogueDescriptor(componentProfile, callableContract: StageContract(("bundle", json), ("catalog", json))),
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
    .AdmitAsync(plan, cancellationToken: cts.Token);
if (!admission.Succeeded)
{
    Console.Error.WriteLine("Admission failed:");
    foreach (var diagnostic in admission.Diagnostics)
        Console.Error.WriteLine($"  {diagnostic.Code}: {diagnostic.Message} path={diagnostic.Path}");
    return 1;
}

var domainExecutor = services.GetRequiredService<PlanningDomainDiscoveryExecutor>();
var topologyExecutor = services.GetRequiredService<PlanningTopologyExecutor>();
var contractExecutor = services.GetRequiredService<PlanningContractExecutor>();
var componentExecutor = services.GetRequiredService<PlanningComponentExecutor>();
var ports = new FuwenZhinuExecutionPorts(
    services.GetRequiredService<BundleContractInputsActivity>(),
    new EchoRequestContext(),
    new LiveStageRouter(domainExecutor, topologyExecutor, contractExecutor, componentExecutor));
var registration = await new FuwenZhinuWorkflowFactory(
        new InMemoryWorkflowDefinitionStore(),
        new FuwenZhinuProviderRuntimeIdentity(
            admission.Receipt!.CatalogueSnapshotRevision,
            admission.Receipt.ResolvedDescriptorSetFingerprint),
        ports)
    .CreateAsync("guyabano.fuwen-planning", "1", admission, cts.Token);

Directory.CreateDirectory(workdir);
var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
{
    DatabasePath = Path.Combine(workdir, "workflow.db"),
    Pooling = false,
});
await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
    new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(100) });

using var input = JsonDocument.Parse(JsonSerializer.Serialize(request));
var runId = await engine.StartAsync("guyabano.fuwen-planning", "1", input.RootElement.Clone(), cancellationToken: cts.Token);
await engine.ExecuteAsync(runId, cts.Token);
var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: cts.Token);
Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
Console.Error.WriteLine($"Completed run {runId:D} in {workdir}.");
return 0;

sealed class EchoRequestContext : IContextProvider
{
    public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken ct = default)
    {
        // Repo-context assembly (BuildPlanningRequest concatenation) is not
        // yet ported; the raw request flows through unchanged.
        var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.Clone();
        var snap = new ContextSnapshotReference(request.Provider, "snap-live",
            new ContentDigest("sha256", "request/v1", new string('c', 64)),
            new ContentDigest("sha256", "content/v1", new string('d', 64)), [],
            "policy/1", new ContextSnapshotBudgetEvidence(false, null, null, null, null), DateTimeOffset.UtcNow);
        return ValueTask.FromResult(ContextExecutionResult.Succeeded(RuntimeValue.FromJson(input), snap));
    }
}

sealed class LiveStageRouter(
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
