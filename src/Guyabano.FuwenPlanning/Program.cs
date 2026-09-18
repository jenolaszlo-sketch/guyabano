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
var assembleActivity = PlanningFuwenDescriptors.AssembleActivity();
var gapActivity = PlanningFuwenDescriptors.GapActivity(
    await PackBytes(promptsRoot, "planning-gap-resolution", "system.sbn", cts.Token),
    await PackBytes(promptsRoot, "planning-gap-resolution", "user.sbn", cts.Token));
var assessActivity = PlanningFuwenDescriptors.AssessActivity();
var applyActivity = PlanningFuwenDescriptors.ApplyGuidanceActivity();
var (attemptSchemaDescriptor, attemptSchema) = PlanningFuwenDescriptors.StageAttemptSchema();
var attemptType = new NamedTypeReference(attemptSchemaDescriptor);
var (jointSchemaDescriptor, jointSchema) = PlanningFuwenDescriptors.JointAttemptSchema();
var jointType = new NamedTypeReference(jointSchemaDescriptor);
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
        StageContract(attemptType, ("bundle", json), ("previousFailure", optionalStr))),
    new TrustedCatalogueDescriptor(componentProfile, callableContract:
        StageContract(attemptType, ("bundle", json), ("catalog", json), ("previousFailure", optionalStr))),
    new TrustedCatalogueDescriptor(domainTemplate),
    new TrustedCatalogueDescriptor(topologyTemplate),
    new TrustedCatalogueDescriptor(contractTemplate),
    new TrustedCatalogueDescriptor(componentTemplate),
    new TrustedCatalogueDescriptor(gapActivity, callableContract:
        StageContract(str, ("request", str), ("attempt", jointType))),
    new TrustedCatalogueDescriptor(assessActivity, callableContract:
        StageContract(jointType, ("bundle", json), ("contract", attemptType), ("manifest", attemptType))),
    new TrustedCatalogueDescriptor(applyActivity, callableContract:
        StageContract(jointType, ("attempt", jointType), ("guidance", str))),
    new TrustedCatalogueDescriptor(assembleActivity, callableContract: new CallableContract(
        new CallableSignature(
            [
                new CallableParameter("domain", json),
                new CallableParameter("topology", json),
                new CallableParameter("catalogs", list8),
                new CallableParameter("manifests", list8),
            ], json),
        CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    new TrustedCatalogueDescriptor(attemptSchemaDescriptor, schemaDefinition: attemptSchema),
    new TrustedCatalogueDescriptor(jointSchemaDescriptor, schemaDefinition: jointSchema),
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
var contractExecutor = new PlanningContractExecutor(
    router,
    services.GetRequiredService<IPromptBuilder<ContractDesignPromptContext>>(),
    repairer, options.PlannerModel, options.ContractMaxTokens, outputEnvelope: true);
var componentExecutor = new PlanningComponentExecutor(
    router,
    services.GetRequiredService<IPromptBuilder<ComponentDesignPromptContext>>(),
    repairer, options.PlannerModel, options.ComponentMaxTokens, outputEnvelope: true);
var activityRouter = new LiveActivityRouter(
    services.GetRequiredService<BundleContractInputsActivity>(),
    new AssessContextDesignActivity(),
    new ApplyGuidanceActivity(),
    new ResolveStageGuidanceActivity(
        router,
        services.GetRequiredService<IPromptBuilder<PlanningGapResolutionPromptContext>>(),
        repairer, options.PlannerModel, 6000),
    services.GetRequiredService<AssemblePlanningActivity>());
var ports = new FuwenZhinuExecutionPorts(
    activityRouter,
    services.GetRequiredService<PlanningRequestContextProvider>(),
    new LiveStageRouter(
        domainExecutor,
        topologyExecutor,
        contractExecutor,
        componentExecutor));

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

// Persist admitted artifacts so a later failure never loses completed work.
static async Task PersistAsync(string workdir, string name, string content, CancellationToken ct)
{
    var path = Path.Combine(workdir, "artifacts", name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, content, ct);
}
await PersistAsync(workdir, "domain.json", domainJson, cts.Token);
await PersistAsync(workdir, "topology.json", topologyJson, cts.Token);

// Phase D: per-context design loops in topological order, each with gap
// resolution, harvesting artifacts for the next context's bundle.
static JsonElement BundleLiteral(
    JsonElement context, string domainJson, string topologyJson,
    List<string> upstreamCatalogs, List<string> upstreamManifests)
{
    using var domain = JsonDocument.Parse(domainJson);
    using var topology = JsonDocument.Parse(topologyJson);
    var catalogs = new System.Text.Json.Nodes.JsonArray();
    foreach (var catalog in upstreamCatalogs)
        catalogs.Add(System.Text.Json.Nodes.JsonNode.Parse(catalog));
    var manifests = new System.Text.Json.Nodes.JsonArray();
    foreach (var manifest in upstreamManifests)
        manifests.Add(System.Text.Json.Nodes.JsonNode.Parse(manifest));
    var bundle = new System.Text.Json.Nodes.JsonObject
    {
        ["context"] = System.Text.Json.Nodes.JsonNode.Parse(context.GetRawText()),
        ["domain"] = System.Text.Json.Nodes.JsonNode.Parse(domain.RootElement.GetRawText()),
        ["topology"] = System.Text.Json.Nodes.JsonNode.Parse(topology.RootElement.GetRawText()),
        ["upstreamCatalogs"] = catalogs,
        ["upstreamManifests"] = manifests,
    };
    return JsonDocument.Parse(bundle.ToJsonString()).RootElement.Clone();
}
var topologyDocument = JsonDocument.Parse(topologyJson);
var orderedContexts = TopologicalContexts.Order(
    topologyDocument.RootElement.GetProperty("boundedContexts").EnumerateArray()
        .Select(element => JsonSerializer.Deserialize<BoundedContextPlan>(element.GetRawText())!)
        .ToArray());
var harvestedCatalogs = new List<string>();
var harvestedManifests = new List<string>();
foreach (var context in orderedContexts)
{
    var loopPath = StructuralNodeIdentity.Create("phaseD", "design");
    var contractPath = loopPath + "/$body/contract";
    var manifestPath = loopPath + "/$body/manifest";
    var assessPath = loopPath + "/$body/assess";
    var gapPath = loopPath + "/$body/gap";
    var applyPath = loopPath + "/$body/apply";
    var returnPathD = StructuralNodeIdentity.Create("phaseD", "return_result");
    var bundleJson = BundleLiteral(
        JsonDocument.Parse(JsonSerializer.Serialize(context)).RootElement,
        domainJson, topologyJson, harvestedCatalogs, harvestedManifests).GetRawText();
    var planD = new WorkflowPlanBuilder("phaseD", "1", str, jointType, "routing/1")
        .AddSchema(jointSchema)
        .AddSchema(attemptSchema)
        .AddNode(new RepeatNode(
            "design", loopPath, 3, jointType,
            new LiteralBinding(JsonDocument.Parse(
                """{"ok":false,"catalog":null,"manifest":null,"error":"","retryStage":null,"retryArtifact":null}""").RootElement.Clone()),
            [
                new InferenceNode("contract", contractPath, contractProfile, contractTemplate,
                    [
                        new ArgumentBinding("bundle", new LiteralBinding(JsonDocument.Parse(bundleJson).RootElement.Clone())),
                        new ArgumentBinding("previousFailure", new LoopStateBinding(["error"])),
                    ], [], attemptType, []),
                new InferenceNode("manifest", manifestPath, componentProfile, componentTemplate,
                    [
                        new ArgumentBinding("bundle", new LiteralBinding(JsonDocument.Parse(bundleJson).RootElement.Clone())),
                        new ArgumentBinding("catalog", new NodeOutputBinding(contractPath, [])),
                        new ArgumentBinding("previousFailure", new LoopStateBinding(["error"])),
                    ], [], attemptType, []),
                new ActivityNode("assess", assessPath, assessActivity,
                    [
                        new ArgumentBinding("bundle", new LiteralBinding(JsonDocument.Parse(bundleJson).RootElement.Clone())),
                        new ArgumentBinding("contract", new NodeOutputBinding(contractPath, [])),
                        new ArgumentBinding("manifest", new NodeOutputBinding(manifestPath, [])),
                    ], jointType),
                new ActivityNode("gap", gapPath, gapActivity,
                    [
                        new ArgumentBinding("request", new LiteralBinding(JsonDocument.Parse(JsonSerializer.Serialize(assembled)).RootElement.Clone())),
                        new ArgumentBinding("attempt", new NodeOutputBinding(assessPath, [])),
                    ], str),
                new ActivityNode("apply", applyPath, applyActivity,
                    [
                        new ArgumentBinding("attempt", new NodeOutputBinding(assessPath, [])),
                        new ArgumentBinding("guidance", new NodeOutputBinding(gapPath, [])),
                    ], jointType),
            ],
            new NodeOutputBinding(applyPath, []),
            new ConditionExpression(ConditionOperator.Equal,
                new NodeOutputBinding(assessPath, ["ok"]),
                new LiteralBinding(breakLiteral.RootElement.Clone())),
            jointType))
        .AddNode(new ReturnNode("return_result", returnPathD, new NodeOutputBinding(loopPath, [])))
        .SetExecutionOrder(new WorkflowExecutionOrder([
            new WorkflowExecutionRegion("phaseD", [
                new WorkflowExecutionPhase([loopPath]),
                new WorkflowExecutionPhase([returnPathD]),
            ]),
            new WorkflowExecutionRegion("phaseD/design/$body", [
                new WorkflowExecutionPhase([contractPath]),
                new WorkflowExecutionPhase([manifestPath]),
                new WorkflowExecutionPhase([assessPath]),
                new WorkflowExecutionPhase([gapPath]),
                new WorkflowExecutionPhase([applyPath]),
            ]),
        ]))
        .BuildV6();
    var envelope = await RunAsync($"guyabano.fuwen-phase-d-{context.Name.ToLowerInvariant()}", await AdmitAsync(planD),
        JsonSerializer.Serialize(request));
    if (!envelope.GetProperty("ok").GetBoolean())
        throw new InvalidOperationException(
            $"Context design for '{context.Name}' exhausted retries: " + envelope.GetProperty("error").GetString());
    var catalog = envelope.GetProperty("catalog").GetRawText();
    var manifest = envelope.GetProperty("manifest").GetRawText();
    harvestedCatalogs.Add(catalog);
    harvestedManifests.Add(manifest);
    await PersistAsync(workdir, $"catalog-{context.Name}.json", catalog, cts.Token);
    await PersistAsync(workdir, $"manifest-{context.Name}.json", manifest, cts.Token);
    Console.Error.WriteLine($"Phase D: context '{context.Name}' designed.");
}

// Phase E: assemble the final plan from harvested artifacts.
static ListBinding LiteralList(IEnumerable<string> items) => new(
    items.Select(item => (Binding)new LiteralBinding(JsonDocument.Parse(item).RootElement.Clone())).ToArray());
var assemblePath = StructuralNodeIdentity.Create("phaseE", "assemble");
var returnPathE = StructuralNodeIdentity.Create("phaseE", "return_result");
var planE = new WorkflowPlanBuilder("phaseE", "1", json, json, "routing/1")
    .AddNode(new ActivityNode("assemble", assemblePath, assembleActivity,
        [
            new ArgumentBinding("domain", new LiteralBinding(JsonDocument.Parse(domainJson).RootElement.Clone())),
            new ArgumentBinding("topology", new LiteralBinding(JsonDocument.Parse(topologyJson).RootElement.Clone())),
            new ArgumentBinding("catalogs", LiteralList(harvestedCatalogs)),
            new ArgumentBinding("manifests", LiteralList(harvestedManifests)),
        ], json))
    .AddNode(new ReturnNode("return_result", returnPathE, new NodeOutputBinding(assemblePath, [])))
    .SetExecutionOrder(new WorkflowExecutionOrder([
        new WorkflowExecutionRegion("phaseE", [
            new WorkflowExecutionPhase([assemblePath]),
            new WorkflowExecutionPhase([returnPathE]),
        ]),
    ]))
    .BuildV3();
var output = await RunAsync("guyabano.fuwen-phase-e", await AdmitAsync(planE),
    JsonSerializer.Serialize(request));
await PersistAsync(workdir, "plan.json",
    JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }), cts.Token);
Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
Console.Error.WriteLine($"Completed in {workdir}.");
return 0;

sealed class LiveActivityRouter(
    BundleContractInputsActivity bundles,
    AssessContextDesignActivity assess,
    ApplyGuidanceActivity apply,
    ResolveStageGuidanceActivity gap,
    AssemblePlanningActivity assemble) : IActivityExecutor
{
    public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
    {
        var target = request.Activity.Name switch
        {
            "guyabano.bundle-contract-inputs" => (IActivityExecutor)bundles,
            "guyabano.assess-context-design" => assess,
            "guyabano.apply-guidance" => apply,
            "guyabano.resolve-stage-guidance" => gap,
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
