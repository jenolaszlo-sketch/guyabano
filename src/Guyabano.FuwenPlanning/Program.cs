using System.Text.Json;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Planning.Extensions;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Guyabano.Llm.Prompting;
using Guyabano.Llm.Prompting.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Penghou.Baize;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Penghou.Baize.Claude;
using Penghou.Baize.Gemini;
using Penghou.Baize.Ollama;
using Penghou.Baize.OpenAi;
using Penghou.Baize.Router;
using Penghou.Baize.Router.Extensions;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Extensions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

// Live Fuwen-hosted planning run: context -> domain discovery (with
// bounded retries) -> solution topology (with bounded retries) ->
// per-context contract/component fan-out, using the same appsettings.json
// (CodeGeneration + LlmRouting) as the worker.
// Usage: Guyabano.FuwenPlanning "<request>" [--workdir <dir>] [--context-file <path>]
var request = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
string? FlagValue(string flag)
{
    var index = Array.FindIndex(
        args, arg => string.Equals(arg, flag, StringComparison.Ordinal));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var workdir = FlagValue("--workdir")
    ?? Path.Combine(Path.GetTempPath(), "guyabano-fuwen-live", Guid.NewGuid().ToString("N"));
var contextFile = FlagValue("--context-file");
if (string.IsNullOrWhiteSpace(request))
{
    Console.Error.WriteLine("Usage: Guyabano.FuwenPlanning \"<request>\" [--workdir <dir>] [--context-file <path>]");
    return 2;
}
string? repositoryContent = contextFile is not null
    ? await File.ReadAllTextAsync(contextFile)
    : null;

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
var boolean = new PrimitiveType(FuwenPrimitiveKind.Boolean);
var integer = new PrimitiveType(FuwenPrimitiveKind.Integer);
var json = new PrimitiveType(FuwenPrimitiveKind.Json);
var optionalStr = new OptionalType(str);
var list8 = new ListType(json, 8);
var contextDescriptor = PlanningFuwenDescriptors.Context();
var domainProfile = PlanningFuwenDescriptors.StageProfile("domain-discovery", options.PlannerModel, options.DomainMaxTokens);
var topologyProfile = PlanningFuwenDescriptors.StageProfile("solution-topology", options.PlannerModel, options.TopologyMaxTokens);
var contractProfile = PlanningFuwenDescriptors.StageProfile("contract-design", options.PlannerModel, options.ContractMaxTokens);
var componentProfile = PlanningFuwenDescriptors.StageProfile("component-design", options.PlannerModel, options.ComponentMaxTokens);
var bundleActivity = PlanningFuwenDescriptors.BundleActivity();
var (attemptSchemaDescriptor, attemptSchema) = PlanningFuwenDescriptors.StageAttemptSchema();
var attemptType = new NamedTypeReference(attemptSchemaDescriptor);
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
var domainLoopPath = StructuralNodeIdentity.Create("livePlanning", "domainLoop");
var domainPath = domainLoopPath + "/$body/attempt";
// R27: repeat loops can only seed from workflow input, so a topology
// retry loop cannot consume the domain artifact; topology runs as a
// strict single attempt while domain (input-seeded) gets the retry loop.
var topologyPath = StructuralNodeIdentity.Create("livePlanning", "topology");
var bundlePath = StructuralNodeIdentity.Create("livePlanning", "bundles");
var fanOutPath = StructuralNodeIdentity.Create("livePlanning", "design");
var contractPath = fanOutPath + "/$body/contracts";
var manifestPath = fanOutPath + "/$body/manifest";
var returnPath = StructuralNodeIdentity.Create("livePlanning", "return_result");

using var initialEnvelope = JsonDocument.Parse("""{"ok":false,"artifact":null,"error":""}""");
using var breakLiteral = JsonDocument.Parse("true");
var ctxArguments = new List<ArgumentBinding>
{
    new("request", new InputBinding([])),
    new("includeRepositoryContext", new LiteralBinding(
        JsonDocument.Parse(options.IncludeRepositoryContextInPrompts || repositoryContent is not null ? "true" : "false").RootElement.Clone())),
    new("maxCharacters", new LiteralBinding(
        JsonDocument.Parse(options.RepositoryContextMaximumPromptCharacters.ToString()).RootElement.Clone())),
};
if (repositoryContent is not null)
    ctxArguments.Add(new ArgumentBinding("repositoryContext",
        new LiteralBinding(JsonDocument.Parse(JsonSerializer.Serialize(repositoryContent)).RootElement.Clone())));

WorkflowPlanBuilder RepeatStage(
    WorkflowPlanBuilder current,
    string name,
    string loopPath,
    DescriptorReference profile,
    DescriptorReference template,
    List<ArgumentBinding> extraArguments)
{
    var bodyPath = loopPath + "/$body/attempt";
    var arguments = new List<ArgumentBinding>(extraArguments)
    {
        new("previousFailure", new LoopStateBinding(["error"])),
    };
    return current.AddNode(new RepeatNode(
        name, loopPath, 3, attemptType,
        new LiteralBinding(initialEnvelope.RootElement.Clone()),
        [new InferenceNode("attempt", bodyPath, profile, template, arguments, [], attemptType, [])],
        new NodeOutputBinding(bodyPath, []),
        new ConditionExpression(ConditionOperator.Equal,
            new NodeOutputBinding(bodyPath, ["ok"]),
            new LiteralBinding(breakLiteral.RootElement.Clone())),
        attemptType));
}

var planBuilder = new WorkflowPlanBuilder("livePlanning", "1", str, list8, "routing/1")
    .AddSchema(attemptSchema)
    .AddSchema(bundleSchema)
    .AddNode(new ContextNode("ctx", ctxPath, contextDescriptor, ctxArguments, str));
planBuilder = RepeatStage(planBuilder, "domainLoop", domainLoopPath,
    domainProfile, domainTemplate,
    [new ArgumentBinding("request", new InputBinding([]))]);
planBuilder = planBuilder.AddNode(new InferenceNode("topology", topologyPath, topologyProfile, topologyTemplate,
    [
        new ArgumentBinding("request", new NodeOutputBinding(ctxPath, [])),
        new ArgumentBinding("domain", new NodeOutputBinding(domainLoopPath, ["artifact"])),
    ], [], json, []));
var plan = planBuilder
    .AddNode(new ActivityNode("bundles", bundlePath, bundleActivity,
        [
            new ArgumentBinding("topology", new NodeOutputBinding(topologyPath, [])),
            new ArgumentBinding("domain", new NodeOutputBinding(domainLoopPath, ["artifact"])),
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
            new WorkflowExecutionPhase([domainLoopPath]),
            new WorkflowExecutionPhase([topologyPath]),
            new WorkflowExecutionPhase([bundlePath]),
            new WorkflowExecutionPhase([fanOutPath]),
            new WorkflowExecutionPhase([returnPath]),
        ]),
        new WorkflowExecutionRegion("livePlanning/domainLoop/$body", [
            new WorkflowExecutionPhase([domainPath]),
        ]),
        new WorkflowExecutionRegion("livePlanning/design/$body", [
            new WorkflowExecutionPhase([contractPath]),
            new WorkflowExecutionPhase([manifestPath]),
        ]),
    ]))
    .BuildV6();

static CallableContract StageContract(FuwenType output, params (string Name, FuwenType Type)[] parameters) => new(
    new CallableSignature([.. parameters.Select(p => new CallableParameter(p.Name, p.Type))], output),
    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);
var catalogue = new InMemoryTrustedCatalogue([
    new TrustedCatalogueDescriptor(contextDescriptor, callableContract: new CallableContract(
        new CallableSignature(
            [
                new CallableParameter("request", str),
                new CallableParameter("repositoryContext", optionalStr),
                new CallableParameter("includeRepositoryContext", boolean),
                new CallableParameter("maxCharacters", integer),
            ], str),
        CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    new TrustedCatalogueDescriptor(domainProfile, callableContract:
        StageContract(attemptType, ("request", str), ("previousFailure", optionalStr))),
    new TrustedCatalogueDescriptor(topologyProfile, callableContract:
        StageContract(json, ("request", str), ("domain", json))),
    new TrustedCatalogueDescriptor(contractProfile, callableContract:
        StageContract(json, ("bundle", json))),
    new TrustedCatalogueDescriptor(componentProfile, callableContract:
        StageContract(json, ("bundle", json), ("catalog", json))),
    new TrustedCatalogueDescriptor(domainTemplate),
    new TrustedCatalogueDescriptor(topologyTemplate),
    new TrustedCatalogueDescriptor(contractTemplate),
    new TrustedCatalogueDescriptor(componentTemplate),
    new TrustedCatalogueDescriptor(bundleActivity, callableContract: new CallableContract(
        new CallableSignature(
            [new CallableParameter("topology", json), new CallableParameter("domain", json)], bundleList),
        CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    new TrustedCatalogueDescriptor(attemptSchemaDescriptor, schemaDefinition: attemptSchema),
    new TrustedCatalogueDescriptor(bundleSchemaDescriptor, schemaDefinition: bundleSchema),
]);
var admission = await new WorkflowAdmissionService(
        new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
    .AdmitAsync(plan, cancellationToken: cts.Token);
if (!admission.Succeeded)
{
    Console.Error.WriteLine("Admission failed:");
    foreach (var diagnostic in admission.Diagnostics)
        Console.Error.WriteLine($"  {diagnostic.Code}: {diagnostic.Message} path={diagnostic.Path} expected={diagnostic.Expected} actual={diagnostic.Actual}");
    return 1;
}

var router = services.GetRequiredService<ILlmRouter>();
var repairer = services.GetRequiredService<ILlmStructuredOutputRepairer>();
var domainExecutor = new PlanningDomainDiscoveryExecutor(
    router,
    services.GetRequiredService<IPromptBuilder<DomainDiscoveryPromptContext>>(),
    repairer, options.PlannerModel, options.DomainMaxTokens, outputEnvelope: true);
var topologyExecutor = new PlanningTopologyExecutor(
    router,
    services.GetRequiredService<IPromptBuilder<SolutionTopologyPromptContext>>(),
    repairer, options.PlannerModel, options.TopologyMaxTokens, outputEnvelope: true);
var ports = new FuwenZhinuExecutionPorts(
    services.GetRequiredService<BundleContractInputsActivity>(),
    services.GetRequiredService<PlanningRequestContextProvider>(),
    new LiveStageRouter(
        domainExecutor,
        topologyExecutor,
        services.GetRequiredService<PlanningContractExecutor>(),
        services.GetRequiredService<PlanningComponentExecutor>()));
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
