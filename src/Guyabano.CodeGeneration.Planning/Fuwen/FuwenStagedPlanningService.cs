using System.Text.Json;
using System.Text.Json.Nodes;
using Guyabano.Llm.Prompting;
using Microsoft.Extensions.Options;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// <see cref="ICodeGenerationPlanningService"/> implemented as phased
/// Fuwen pipelines instead of hard-coded stage calls, so the web app and
/// worker can consume durable planning through the same interface.
/// Phases (each admitted and run as a static plan, R27):
/// domain retry loop, topology retry loop, one retry loop per bounded
/// context in topological order (upstream embedded as bundle literals),
/// then plan assembly. Single architecture pass; gap resolution runs
/// inside every loop via the resolver pack.
/// </summary>
public sealed class FuwenStagedPlanningService(
    ILlmRouter llmRouter,
    IPromptBuilder<DomainDiscoveryPromptContext> domainBuilder,
    IPromptBuilder<SolutionTopologyPromptContext> topologyBuilder,
    IPromptBuilder<ContractDesignPromptContext> contractBuilder,
    IPromptBuilder<ComponentDesignPromptContext> componentBuilder,
    IPromptBuilder<PlanningGapResolutionPromptContext> gapBuilder,
    ILlmStructuredOutputRepairer repairer,
    IPromptLoader promptLoader,
    IOptions<FuwenPlanningOptions> planningOptions) : ICodeGenerationPlanningService
{
    public async Task<CodeGenerationPlanningOutcome> PlanAsync(
        string request,
        string model,
        int maxTokens = 12000,
        string? previousFailure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var tokens = planningOptions.Value;
        var pack = async (string name, string file) =>
            System.Text.Encoding.UTF8.GetBytes(
                await promptLoader.LoadAsync($"{name}/{file}", cancellationToken));
        var domainTemplate = PlanningFuwenDescriptors.Template(
            "domain-discovery", "Guyabano.CodeGeneration.Planning.DomainDiscovery",
            await pack("domain-discovery", "system.sbn"), await pack("domain-discovery", "user.sbn"));
        var topologyTemplate = PlanningFuwenDescriptors.Template(
            "solution-topology", "Guyabano.CodeGeneration.Planning.SolutionTopology",
            await pack("solution-topology", "system.sbn"), await pack("solution-topology", "user.sbn"));
        var contractTemplate = PlanningFuwenDescriptors.Template(
            "contract-design", "Guyabano.CodeGeneration.Planning.BoundedContextContractCatalog",
            await pack("contract-design", "system.sbn"), await pack("contract-design", "user.sbn"));
        var componentTemplate = PlanningFuwenDescriptors.Template(
            "component-design", "Guyabano.CodeGeneration.Planning.BoundedContextComponentManifest",
            await Pack("component-design", "system.sbn"), await Pack("component-design", "user.sbn"));
        async Task<byte[]> Pack(string name, string file) =>
            System.Text.Encoding.UTF8.GetBytes(await promptLoader.LoadAsync($"{name}/{file}", cancellationToken));

        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        var optionalStr = new OptionalType(str);
        var list8 = new ListType(json, 8);
        var domainProfile = PlanningFuwenDescriptors.StageProfile("domain-discovery", model, tokens.DomainMaxTokens);
        var topologyProfile = PlanningFuwenDescriptors.StageProfile("solution-topology", model, tokens.TopologyMaxTokens);
        var contractProfile = PlanningFuwenDescriptors.StageProfile("contract-design", model, tokens.ContractMaxTokens);
        var componentProfile = PlanningFuwenDescriptors.StageProfile("component-design", model, tokens.ComponentMaxTokens);
        var gapBytes = await Pack("planning-gap-resolution", "system.sbn");
        var gapUserBytes = await Pack("planning-gap-resolution", "user.sbn");
        var gapActivity = PlanningFuwenDescriptors.GapActivity(gapBytes, gapUserBytes);
        var assessActivity = PlanningFuwenDescriptors.AssessActivity();
        var applyActivity = PlanningFuwenDescriptors.ApplyGuidanceActivity();
        var assembleActivity = PlanningFuwenDescriptors.AssembleActivity();
        var (attemptSchemaDescriptor, attemptSchema) = PlanningFuwenDescriptors.StageAttemptSchema();
        var attemptType = new NamedTypeReference(attemptSchemaDescriptor);
        var (jointSchemaDescriptor, jointSchema) = PlanningFuwenDescriptors.JointAttemptSchema();
        var jointType = new NamedTypeReference(jointSchemaDescriptor);

        static CallableContract StageContract(FuwenType output, params (string Name, FuwenType Type)[] parameters) => new(
            new CallableSignature([.. parameters.Select(p => new CallableParameter(p.Name, p.Type))], output),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(domainProfile, callableContract:
                StageContract(attemptType, ("request", str), ("previousFailure", optionalStr))),
            new TrustedCatalogueDescriptor(topologyProfile, callableContract:
                StageContract(attemptType, ("request", str), ("domain", json), ("previousFailure", optionalStr))),
            new TrustedCatalogueDescriptor(contractProfile, callableContract:
                StageContract(attemptType, ("bundle", json), ("previousFailure", optionalStr))),
            new TrustedCatalogueDescriptor(componentProfile, callableContract:
                StageContract(attemptType, ("bundle", json), ("catalog", attemptType), ("previousFailure", optionalStr))),
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

        var domainExecutor = new PlanningDomainDiscoveryExecutor(
            llmRouter, domainBuilder, repairer, model, tokens.DomainMaxTokens, outputEnvelope: true);
        var topologyExecutor = new PlanningTopologyExecutor(
            llmRouter, topologyBuilder, repairer, model, tokens.TopologyMaxTokens, outputEnvelope: true);
        var contractExecutor = new PlanningContractExecutor(
            llmRouter, contractBuilder, repairer, model, tokens.ContractMaxTokens, outputEnvelope: true);
        var componentExecutor = new PlanningComponentExecutor(
            llmRouter, componentBuilder, repairer, model, tokens.ComponentMaxTokens, outputEnvelope: true);
        var gapExecutor = new ResolveStageGuidanceActivity(
            llmRouter, gapBuilder, repairer, model, 6000);
        var ports = new FuwenZhinuExecutionPorts(
            new FuwenActivityRouter(
                new AssessContextDesignActivity(),
                new ApplyGuidanceActivity(),
                gapExecutor,
                new AssemblePlanningActivity()),
            new UnusedContextProvider(),
            new FuwenInferenceRouter(
                domainExecutor, topologyExecutor, contractExecutor, componentExecutor));

        var root = Path.Combine(Path.GetTempPath(), "guyabano-fuwen-service", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "workflow.db"),
            Pooling = false,
        });
        try
        {
            var runner = new PhasedRunner(
                store, catalogue, ports,
                attemptType, jointType, attemptSchema, jointSchema,
                domainProfile, topologyProfile, contractProfile, componentProfile,
                domainTemplate, topologyTemplate, contractTemplate, componentTemplate,
                gapActivity, assessActivity, applyActivity,
                request, previousFailure, cancellationToken);
            var artifacts = await runner.RunAsync();
            var plan = StagedCodeGenerationPlanAssembler.Assemble(artifacts);
            return new CodeGenerationPlanningOutcome(
                true, PlanningFailure.None, null, model, plan, false, [])
            {
                StagedArtifacts = artifacts,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CodeGenerationPlanningOutcome(
                false, PlanningFailure.InvalidPlan, exception.Message, model, null, false, []);
        }
    }

    private sealed class FuwenActivityRouter(
        AssessContextDesignActivity assess,
        ApplyGuidanceActivity apply,
        ResolveStageGuidanceActivity gap,
        AssemblePlanningActivity assemble) : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var target = request.Activity.Name switch
            {
                "guyabano.assess-context-design" => (IActivityExecutor)assess,
                "guyabano.apply-guidance" => apply,
                "guyabano.resolve-stage-guidance" => gap,
                "guyabano.assemble-plan" => assemble,
                _ => throw new InvalidOperationException($"Unknown activity '{request.Activity.Name}'."),
            };
            return target.ExecuteAsync(request, ct);
        }
    }

    private sealed class FuwenInferenceRouter(
        PlanningDomainDiscoveryExecutor domain,
        PlanningTopologyExecutor topology,
        PlanningContractExecutor contract,
        PlanningComponentExecutor component) : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken ct = default)
        {
            if (request.PromptTemplate is null)
            {
                throw new InvalidOperationException(
                    "The staged planning router serves template-driven inference only; " +
                    "workflow-owned prompt requests need a prompt-aware executor.");
            }
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

    private sealed class UnusedContextProvider : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("Fuwen staged planning carries request text in literals; no context node is used.");
    }

    private sealed class PhasedRunner(
        SqliteWorkflowStore store,
        InMemoryTrustedCatalogue catalogue,
        FuwenZhinuExecutionPorts ports,
        NamedTypeReference attemptType,
        NamedTypeReference jointType,
        ObjectSchemaDefinition attemptSchema,
        ObjectSchemaDefinition jointSchema,
        DescriptorReference domainProfile,
        DescriptorReference topologyProfile,
        DescriptorReference contractProfile,
        DescriptorReference componentProfile,
        DescriptorReference domainTemplate,
        DescriptorReference topologyTemplate,
        DescriptorReference contractTemplate,
        DescriptorReference componentTemplate,
        DescriptorReference gapActivity,
        DescriptorReference assessActivity,
        DescriptorReference applyActivity,
        string request,
        string? previousFailure,
        CancellationToken ct)
    {
        private static readonly JsonElement TrueLiteral =
            JsonDocument.Parse("true").RootElement.Clone();
        private static readonly JsonElement EmptyGuidance =
            JsonDocument.Parse("\"\"").RootElement.Clone();

        public async Task<StagedPlanningArtifacts> RunAsync()
        {
            var str = new PrimitiveType(FuwenPrimitiveKind.String);
            var json = new PrimitiveType(FuwenPrimitiveKind.Json);
            var registry = new WorkflowRegistry();
            var engine = new WorkflowEngine(store, registry,
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(100) });
            await using var _ = engine;
            var compiler = new WorkflowCompiler(catalogue,
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", []));

            async Task<JsonElement> RunPhaseAsync(string workflowName, WorkflowPlan plan, string inputJson)
            {
                var admission = await new WorkflowAdmissionService(compiler)
                    .AdmitAsync(plan, cancellationToken: ct);
                if (!admission.Succeeded)
                    throw new InvalidOperationException(
                        $"Phase '{workflowName}' admission failed: " +
                        string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
                var registration = await new FuwenZhinuWorkflowFactory(
                        new InMemoryWorkflowDefinitionStore(),
                        new FuwenZhinuProviderRuntimeIdentity(
                            admission.Receipt!.CatalogueSnapshotRevision,
                            admission.Receipt.ResolvedDescriptorSetFingerprint),
                        ports)
                    .CreateAsync(workflowName, "1", admission, ct);
                registration.Register(registry);
                var runId = await engine.StartAsync(workflowName, "1",
                    JsonDocument.Parse(inputJson).RootElement.Clone(), cancellationToken: ct);
                await engine.ExecuteAsync(runId, ct);
                return await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);
            }

            WorkflowPlanBuilder RetryLoop(
                WorkflowPlanBuilder current,
                string workflow, string loopName,
                DescriptorReference profile, DescriptorReference template,
                List<ArgumentBinding> extraArguments, string initialError)
            {
                var loopPath = StructuralNodeIdentity.Create(workflow, loopName);
                var bodyPath = loopPath + "/$body/attempt";
                using var initial = JsonDocument.Parse(
                    $$"""{"ok":false,"artifact":null,"error":{{JsonSerializer.Serialize(initialError)}}}""");
                var arguments = new List<ArgumentBinding>(extraArguments)
                {
                    new("previousFailure", new LoopStateBinding(["error"])),
                };
                return current
                    .AddNode(new RepeatNode(
                        loopName, loopPath, 3, attemptType,
                        new LiteralBinding(initial.RootElement.Clone()),
                        [new InferenceNode("attempt", bodyPath, profile, template, arguments, [], attemptType, [])],
                        new NodeOutputBinding(bodyPath, []),
                        new ConditionExpression(ConditionOperator.Equal,
                            new NodeOutputBinding(bodyPath, ["ok"]),
                            new LiteralBinding(TrueLiteral)),
                        attemptType));
            }

            WorkflowPlan FinishLoop(
                WorkflowPlanBuilder current, string workflow, string loopName)
            {
                var loopPath = StructuralNodeIdentity.Create(workflow, loopName);
                var returnPath = StructuralNodeIdentity.Create(workflow, "return_result");
                return current
                    .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(loopPath, [])))
                    .SetExecutionOrder(new WorkflowExecutionOrder([
                        new WorkflowExecutionRegion(workflow, [
                            new WorkflowExecutionPhase([loopPath]),
                            new WorkflowExecutionPhase([returnPath]),
                        ]),
                        new WorkflowExecutionRegion($"{loopPath}/$body", [
                            new WorkflowExecutionPhase([loopPath + "/$body/attempt"]),
                        ]),
                    ]))
                    .BuildV6();
            }

            static JsonElement Literal(string value) =>
                JsonDocument.Parse(value).RootElement.Clone();

            // Domain with retries, seeded from the outer failure when present.
            var domainPlan = FinishLoop(RetryLoop(
                    new WorkflowPlanBuilder("stagedDomain", "1", str, attemptType, "routing/1")
                        .AddSchema(attemptSchema),
                    "stagedDomain", "domainLoop", domainProfile, domainTemplate,
                    [new ArgumentBinding("request", new LiteralBinding(Literal(JsonSerializer.Serialize(request))))],
                    previousFailure ?? string.Empty),
                "stagedDomain", "domainLoop");
            var domainEnvelope = await RunPhaseAsync(
                "guyabano.fuwen-domain", domainPlan, JsonSerializer.Serialize(request));
            if (!domainEnvelope.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException(
                    "Domain discovery exhausted retries: " + domainEnvelope.GetProperty("error").GetString());
            var domainJson = domainEnvelope.GetProperty("artifact").GetRawText();
            var domain = JsonSerializer.Deserialize<DomainDiscovery>(domainJson)
                ?? throw new InvalidOperationException("Domain stage produced an unreadable artifact.");

            // Topology with retries, domain embedded (R27).
            var topologyPlan = FinishLoop(RetryLoop(
                    new WorkflowPlanBuilder("stagedTopology", "1", str, attemptType, "routing/1")
                        .AddSchema(attemptSchema),
                    "stagedTopology", "topologyLoop", topologyProfile, topologyTemplate,
                    [
                        new ArgumentBinding("request", new LiteralBinding(Literal(JsonSerializer.Serialize(request)))),
                        new ArgumentBinding("domain", new LiteralBinding(JsonDocument.Parse(domainJson).RootElement.Clone())),
                    ],
                    previousFailure ?? string.Empty),
                "stagedTopology", "topologyLoop");
            var topologyEnvelope = await RunPhaseAsync(
                "guyabano.fuwen-topology", topologyPlan, JsonSerializer.Serialize(request));
            if (!topologyEnvelope.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException(
                    "Topology design exhausted retries: " + topologyEnvelope.GetProperty("error").GetString());
            var topologyJson = topologyEnvelope.GetProperty("artifact").GetRawText();
            var topology = JsonSerializer.Deserialize<SolutionTopology>(topologyJson)
                ?? throw new InvalidOperationException("Topology stage produced an unreadable artifact.");

            // One joint retry loop per context in topological order, with
            // upstream artifacts embedded as bundle literals (R27).
            var catalogs = new List<BoundedContextContractCatalog>();
            var manifests = new List<BoundedContextComponentManifest>();
            foreach (var context in TopologicalContexts.Order(topology.BoundedContexts))
            {
                var bundle = new JsonObject
                {
                    ["context"] = JsonNode.Parse(JsonSerializer.Serialize(context)),
                    ["domain"] = JsonNode.Parse(domainJson),
                    ["topology"] = JsonNode.Parse(topologyJson),
                    ["upstreamCatalogs"] = JsonNode.Parse(JsonSerializer.Serialize(catalogs)),
                    ["upstreamManifests"] = JsonNode.Parse(JsonSerializer.Serialize(manifests)),
                }.ToJsonString();
                var joint = await RunContextLoopAsync(
                    context.Name, bundle, compiler, ports, engine, registry, ct);
                var catalog = JsonSerializer.Deserialize<BoundedContextContractCatalog>(
                    joint.GetProperty("catalog").GetRawText())
                    ?? throw new InvalidOperationException($"Context design for '{context.Name}' produced an unreadable catalog.");
                var manifest = JsonSerializer.Deserialize<BoundedContextComponentManifest>(
                    joint.GetProperty("manifest").GetRawText())
                    ?? throw new InvalidOperationException($"Context design for '{context.Name}' produced an unreadable manifest.");
                catalogs.Add(catalog);
                manifests.Add(manifest);
            }

            return new StagedPlanningArtifacts(domain, topology, catalogs, manifests);
        }

        private async Task<JsonElement> RunContextLoopAsync(
            string contextName,
            string bundleJson,
            WorkflowCompiler compiler,
            FuwenZhinuExecutionPorts ports,
            WorkflowEngine engine,
            WorkflowRegistry registry,
            CancellationToken ct)
        {
            var str = new PrimitiveType(FuwenPrimitiveKind.String);
            var json = new PrimitiveType(FuwenPrimitiveKind.Json);
            var loopPath = StructuralNodeIdentity.Create("stagedContext", "design");
            var contractPath = loopPath + "/$body/contract";
            var manifestPath = loopPath + "/$body/manifest";
            var assessPath = loopPath + "/$body/assess";
            var gapPath = loopPath + "/$body/gap";
            var applyPath = loopPath + "/$body/apply";
            var returnPath = StructuralNodeIdentity.Create("stagedContext", "return_result");
            using var bundle = JsonDocument.Parse(bundleJson);
            using var initial = JsonDocument.Parse(
                """{"ok":false,"catalog":null,"manifest":null,"error":"","retryStage":null,"retryArtifact":null}""");
            using var breakLiteral = JsonDocument.Parse("true");
            var plan = new WorkflowPlanBuilder("stagedContext", "1", str, jointType, "routing/1")
                .AddSchema(jointSchema)
                .AddSchema(attemptSchema)
                .AddNode(new RepeatNode(
                    "design", loopPath, 3, jointType,
                    new LiteralBinding(initial.RootElement.Clone()),
                    [
                        new InferenceNode("contract", contractPath, contractProfile, contractTemplate,
                            [
                                new ArgumentBinding("bundle", new LiteralBinding(bundle.RootElement.Clone())),
                                new ArgumentBinding("previousFailure", new LoopStateBinding(["error"])),
                            ], [], attemptType, []),
                        new InferenceNode("manifest", manifestPath, componentProfile, componentTemplate,
                            [
                                new ArgumentBinding("bundle", new LiteralBinding(bundle.RootElement.Clone())),
                                new ArgumentBinding("catalog", new NodeOutputBinding(contractPath, [])),
                                new ArgumentBinding("previousFailure", new LoopStateBinding(["error"])),
                            ], [], attemptType, []),
                        new ActivityNode("assess", assessPath, assessActivity,
                            [
                                new ArgumentBinding("bundle", new LiteralBinding(bundle.RootElement.Clone())),
                                new ArgumentBinding("contract", new NodeOutputBinding(contractPath, [])),
                                new ArgumentBinding("manifest", new NodeOutputBinding(manifestPath, [])),
                            ], jointType),
                        new ActivityNode("gap", gapPath, gapActivity,
                            [
                                new ArgumentBinding("request", new InputBinding([])),
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
                .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(loopPath, [])))
                .SetExecutionOrder(new WorkflowExecutionOrder([
                    new WorkflowExecutionRegion("stagedContext", [
                        new WorkflowExecutionPhase([loopPath]),
                        new WorkflowExecutionPhase([returnPath]),
                    ]),
                    new WorkflowExecutionRegion("stagedContext/design/$body", [
                        new WorkflowExecutionPhase([contractPath]),
                        new WorkflowExecutionPhase([manifestPath]),
                        new WorkflowExecutionPhase([assessPath]),
                        new WorkflowExecutionPhase([gapPath]),
                        new WorkflowExecutionPhase([applyPath]),
                    ]),
                ]))
                .BuildV6();
            var admission = await new WorkflowAdmissionService(compiler)
                .AdmitAsync(plan, cancellationToken: ct);
            if (!admission.Succeeded)
                throw new InvalidOperationException(
                    $"Context design admission failed: " +
                    string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
            var workflowName = "guyabano.fuwen-context-" + string.Concat(
                contextName.Where(char.IsLetterOrDigit)).ToLowerInvariant();
            var registration = await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    new FuwenZhinuProviderRuntimeIdentity(
                        admission.Receipt!.CatalogueSnapshotRevision,
                        admission.Receipt.ResolvedDescriptorSetFingerprint),
                    ports)
                .CreateAsync(workflowName, "1", admission, ct);
            registration.Register(registry);
            var runId = await engine.StartAsync(workflowName, "1",
                JsonDocument.Parse(JsonSerializer.Serialize(request)).RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);
            if (!output.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException(
                    $"Context design for '{contextName}' exhausted retries: " + output.GetProperty("error").GetString());
            return output.Clone();
        }
    }
}
