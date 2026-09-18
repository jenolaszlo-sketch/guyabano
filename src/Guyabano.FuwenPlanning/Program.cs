using System.Text.Json;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Planning.Extensions;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Guyabano.Llm.Prompting;
using Guyabano.Llm.Prompting.Extensions;
using Penghou.Baize.Router;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Penghou.Baize;
using Penghou.Baize.Claude;
using Penghou.Baize.Gemini;
using Penghou.Baize.Ollama;
using Penghou.Baize.OpenAi;
using Penghou.Baize.Router.Extensions;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Extensions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

// Live Fuwen-hosted planning run in closed-region-respecting phases
// (R27: retry bodies cannot read parent-region outputs, so each phase
// harvests artifacts and the next phase embeds them as literals):
//   A: request assembly (repo context, truncation, markers)
//   B: domain discovery with bounded retries
//   C: solution topology with bounded retries (domain embedded)
//   D: per-context contract/component fan-out + plan assembly
// Independent contexts only; dependent chains need per-context phases.
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
var pairActivity = PlanningFuwenDescriptors.PairActivity();
var assembleActivity = PlanningFuwenDescriptors.AssembleActivity();
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
        StageContract(attemptType, ("request", str), ("domain", json), ("previousFailure", optionalStr))),
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
    new TrustedCatalogueDescriptor(pairActivity, callableContract: new CallableContract(
        new CallableSignature(
            [new CallableParameter("catalog", json), new CallableParameter("manifest", json)], json),
        CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    new TrustedCatalogueDescriptor(assembleActivity, callableContract: new CallableContract(
        new CallableSignature(
            [
                new CallableParameter("domain", json),
                new CallableParameter("topology", json),
                new CallableParameter("pairs", list8),
            ], json),
        CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    new TrustedCatalogueDescriptor(attemptSchemaDescriptor, schemaDefinition: attemptSchema),
    new TrustedCatalogueDescriptor(bundleSchemaDescriptor, schemaDefinition: bundleSchema),
]);
var admissionService = new WorkflowAdmissionService(
    new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])));
async Task<WorkflowAdmissionResult> AdmitAsync(WorkflowPlan plan)
{
    var admission = await admissionService.AdmitAsync(plan, cancellationToken: cts.Token);
    if (!admission.Succeeded)
    {
        Console.Error.WriteLine("Admission failed:");
        foreach (var diagnostic in admission.Diagnostics)
            Console.Error.WriteLine($"  {diagnostic.Code}: {diagnostic.Message} path={diagnostic.Path} expected={diagnostic.Expected} actual={diagnostic.Actual}");
        Environment.Exit(1);
    }
    return admission;
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
var activityRouter = new LiveActivityRouter(
    services.GetRequiredService<BundleContractInputsActivity>(),
    new PackPairActivity(),
    services.GetRequiredService<AssemblePlanningActivity>());
var ports = new FuwenZhinuExecutionPorts(
    activityRouter,
    services.GetRequiredService<PlanningRequestContextProvider>(),
    new LiveStageRouter(
        domainExecutor,
        topologyExecutor,
        services.GetRequiredService<PlanningContractExecutor>(),
        services.GetRequiredService<PlanningComponentExecutor>()));

Directory.CreateDirectory(workdir);
var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
{
    DatabasePath = Path.Combine(workdir, "workflow.db"),
    Pooling = false,
});
var workflowRegistry = new WorkflowRegistry();
var engine = new WorkflowEngine(store, workflowRegistry,
    new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(100) });
await using var _ = engine;
async Task<JsonElement> RunAsync(string workflowName, WorkflowAdmissionResult admission, string inputJson)
{
    var registration = await new FuwenZhinuWorkflowFactory(
            new InMemoryWorkflowDefinitionStore(),
            new FuwenZhinuProviderRuntimeIdentity(
                admission.Receipt!.CatalogueSnapshotRevision,
                admission.Receipt.ResolvedDescriptorSetFingerprint),
            ports)
        .CreateAsync(workflowName, "1", admission, cts.Token);
    registration.Register(workflowRegistry);
    var runId = await engine.StartAsync(workflowName, "1",
        JsonDocument.Parse(inputJson).RootElement.Clone(), cancellationToken: cts.Token);
    await engine.ExecuteAsync(runId, cts.Token);
    return await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: cts.Token);
}

using var initialEnvelope = JsonDocument.Parse("""{"ok":false,"artifact":null,"error":""}""");
using var breakLiteral = JsonDocument.Parse("true");
WorkflowPlanBuilder RetryLoop(
    WorkflowPlanBuilder current,
    string workflow,
    string loopName,
    DescriptorReference profile,
    DescriptorReference template,
    List<ArgumentBinding> extraArguments)
{
    var loopPath = StructuralNodeIdentity.Create(workflow, loopName);
    var bodyPath = loopPath + "/$body/attempt";
    var arguments = new List<ArgumentBinding>(extraArguments)
    {
        new("previousFailure", new LoopStateBinding(["error"])),
    };
    return current.AddNode(new RepeatNode(
        loopName, loopPath, 3, attemptType,
        new LiteralBinding(initialEnvelope.RootElement.Clone()),
        [new InferenceNode("attempt", bodyPath, profile, template, arguments, [], attemptType, [])],
        new NodeOutputBinding(bodyPath, []),
        new ConditionExpression(ConditionOperator.Equal,
            new NodeOutputBinding(bodyPath, ["ok"]),
            new LiteralBinding(breakLiteral.RootElement.Clone())),
        attemptType));
}

// Phase A: request assembly.
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
var ctxPathA = StructuralNodeIdentity.Create("phaseA", "ctx");
var returnPathA = StructuralNodeIdentity.Create("phaseA", "return_result");
var planA = new WorkflowPlanBuilder("phaseA", "1", str, str, "routing/1")
    .AddNode(new ContextNode("ctx", ctxPathA, contextDescriptor, ctxArguments, str))
    .AddNode(new ReturnNode("return_result", returnPathA, new NodeOutputBinding(ctxPathA, [])))
    .SetExecutionOrder(new WorkflowExecutionOrder([
        new WorkflowExecutionRegion("phaseA", [
            new WorkflowExecutionPhase([ctxPathA]),
            new WorkflowExecutionPhase([returnPathA]),
        ]),
    ]))
    .BuildV3();
var assembled = (await RunAsync("guyabano.fuwen-phase-a", await AdmitAsync(planA),
    JsonSerializer.Serialize(request))).GetString()!;
Console.Error.WriteLine("Phase A: request assembled.");

// Phase B: domain discovery with retries (request embedded: retry bodies
// cannot read parent-region outputs, R27).
var domainLoopPath = StructuralNodeIdentity.Create("phaseB", "domainLoop");
var returnPathB = StructuralNodeIdentity.Create("phaseB", "return_result");
var planB = RetryLoop(
    new WorkflowPlanBuilder("phaseB", "1", str, attemptType, "routing/1")
        .AddSchema(attemptSchema),
    "phaseB", "domainLoop", domainProfile, domainTemplate,
    [new ArgumentBinding("request", new LiteralBinding(JsonDocument.Parse(JsonSerializer.Serialize(assembled)).RootElement.Clone()))])
    .AddNode(new ReturnNode("return_result", returnPathB, new NodeOutputBinding(domainLoopPath, [])))
    .SetExecutionOrder(new WorkflowExecutionOrder([
        new WorkflowExecutionRegion("phaseB", [
            new WorkflowExecutionPhase([domainLoopPath]),
            new WorkflowExecutionPhase([returnPathB]),
        ]),
        new WorkflowExecutionRegion("phaseB/domainLoop/$body", [
            new WorkflowExecutionPhase([domainLoopPath + "/$body/attempt"]),
        ]),
    ]))
    .BuildV6();
var domainEnvelope = await RunAsync("guyabano.fuwen-phase-b", await AdmitAsync(planB),
    JsonSerializer.Serialize(request));
if (!domainEnvelope.GetProperty("ok").GetBoolean())
    throw new InvalidOperationException("Domain discovery exhausted retries: " + domainEnvelope.GetProperty("error").GetString());
var domainJson = domainEnvelope.GetProperty("artifact").GetRawText();
Console.Error.WriteLine("Phase B: domain discovery completed.");

// Phase C: topology with retries (domain embedded, R27).
var topologyLoopPath = StructuralNodeIdentity.Create("phaseC", "topologyLoop");
var returnPathC = StructuralNodeIdentity.Create("phaseC", "return_result");
var planC = RetryLoop(
    new WorkflowPlanBuilder("phaseC", "1", str, attemptType, "routing/1")
        .AddSchema(attemptSchema),
    "phaseC", "topologyLoop", topologyProfile, topologyTemplate,
    [
        new ArgumentBinding("request", new LiteralBinding(JsonDocument.Parse(JsonSerializer.Serialize(assembled)).RootElement.Clone())),
        new ArgumentBinding("domain", new LiteralBinding(JsonDocument.Parse(domainJson).RootElement.Clone())),
    ])
    .AddNode(new ReturnNode("return_result", returnPathC, new NodeOutputBinding(topologyLoopPath, [])))
    .SetExecutionOrder(new WorkflowExecutionOrder([
        new WorkflowExecutionRegion("phaseC", [
            new WorkflowExecutionPhase([topologyLoopPath]),
            new WorkflowExecutionPhase([returnPathC]),
        ]),
        new WorkflowExecutionRegion("phaseC/topologyLoop/$body", [
            new WorkflowExecutionPhase([topologyLoopPath + "/$body/attempt"]),
        ]),
    ]))
    .BuildV6();
var topologyEnvelope = await RunAsync("guyabano.fuwen-phase-c", await AdmitAsync(planC),
    JsonSerializer.Serialize(request));
if (!topologyEnvelope.GetProperty("ok").GetBoolean())
    throw new InvalidOperationException("Topology design exhausted retries: " + topologyEnvelope.GetProperty("error").GetString());
var topologyJson = topologyEnvelope.GetProperty("artifact").GetRawText();
Console.Error.WriteLine("Phase C: topology design completed.");

// Phase D: bundles, per-context fan-out, pair packing, and assembly in one
// plan (all same-region wiring, no loops needed).
var bundlePath = StructuralNodeIdentity.Create("phaseD", "bundles");
var fanOutPath = StructuralNodeIdentity.Create("phaseD", "design");
var contractPath = fanOutPath + "/$body/contracts";
var manifestPath = fanOutPath + "/$body/manifest";
var pairPath = fanOutPath + "/$body/pair";
var assemblePath = StructuralNodeIdentity.Create("phaseD", "assemble");
var returnPathD = StructuralNodeIdentity.Create("phaseD", "return_result");
var planD = new WorkflowPlanBuilder("phaseD", "1", str, json, "routing/1")
    .AddSchema(bundleSchema)
    .AddNode(new ActivityNode("bundles", bundlePath, bundleActivity,
        [
            new ArgumentBinding("topology", new LiteralBinding(JsonDocument.Parse(topologyJson).RootElement.Clone())),
            new ArgumentBinding("domain", new LiteralBinding(JsonDocument.Parse(domainJson).RootElement.Clone())),
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
            new ActivityNode("pair", pairPath, pairActivity,
                [
                    new ArgumentBinding("catalog", new NodeOutputBinding(contractPath, [])),
                    new ArgumentBinding("manifest", new NodeOutputBinding(manifestPath, [])),
                ], json),
        ],
        new NodeOutputBinding(pairPath, []),
        list8,
        8, 4))
    .AddNode(new ActivityNode("assemble", assemblePath, assembleActivity,
        [
            new ArgumentBinding("domain", new LiteralBinding(JsonDocument.Parse(domainJson).RootElement.Clone())),
            new ArgumentBinding("topology", new LiteralBinding(JsonDocument.Parse(topologyJson).RootElement.Clone())),
            new ArgumentBinding("pairs", new NodeOutputBinding(fanOutPath, [])),
        ], json))
    .AddNode(new ReturnNode("return_result", returnPathD, new NodeOutputBinding(assemblePath, [])))
    .SetExecutionOrder(new WorkflowExecutionOrder([
        new WorkflowExecutionRegion("phaseD", [
            new WorkflowExecutionPhase([bundlePath]),
            new WorkflowExecutionPhase([fanOutPath]),
            new WorkflowExecutionPhase([assemblePath]),
            new WorkflowExecutionPhase([returnPathD]),
        ]),
        new WorkflowExecutionRegion("phaseD/design/$body", [
            new WorkflowExecutionPhase([contractPath]),
            new WorkflowExecutionPhase([manifestPath]),
            new WorkflowExecutionPhase([pairPath]),
        ]),
    ]))
    .BuildV4();
var output = await RunAsync("guyabano.fuwen-phase-d", await AdmitAsync(planD),
    JsonSerializer.Serialize(request));
Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
Console.Error.WriteLine($"Completed in {workdir}.");
return 0;

sealed class PackPairActivity : IActivityExecutor
{
    public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonElement? catalog = null;
        JsonElement? manifest = null;
        foreach (var argument in request.Arguments)
        {
            if (argument.Value is null)
                continue;
            var json = Penghou.Fuwen.RuntimeValueJson.ToJsonElement(argument.Value);
            if (string.Equals(argument.Name, "catalog", StringComparison.Ordinal))
                catalog = json;
            if (string.Equals(argument.Name, "manifest", StringComparison.Ordinal))
                manifest = json;
        }
        if (catalog is null || manifest is null)
            throw new InvalidOperationException("Pair activity requires catalog and manifest arguments.");
        using var document = JsonDocument.Parse(
            $"{{\"catalog\":{catalog.Value.GetRawText()},\"manifest\":{manifest.Value.GetRawText()}}}");
        return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
            Penghou.Fuwen.RuntimeValue.FromJson(document.RootElement)));
    }
}

sealed class LiveActivityRouter(
    BundleContractInputsActivity bundles,
    PackPairActivity pair,
    AssemblePlanningActivity assemble) : IActivityExecutor
{
    public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
    {
        var target = request.Activity.Name switch
        {
            "guyabano.bundle-contract-inputs" => (IActivityExecutor)bundles,
            "guyabano.pair-artifacts" => pair,
            "guyabano.assemble-plan" => assemble,
            _ => throw new InvalidOperationException($"Unknown activity '{request.Activity.Name}'."),
        };
        return target.ExecuteAsync(request, ct);
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
