#pragma warning disable xUnit1030
using System.Text.Json;
using Penghou.Guihua.Baize;
using Penghou.Guihua;
using System.Text.Json.Nodes;
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
/// Slice 4 of real Guyabano behavior in Fuwen: phased execution of
/// dependent bounded contexts. Each phase admits and runs a static plan;
/// prior-phase artifacts travel as bundle literals because closed regions
/// forbid runtime upstream references inside retry bodies. Per-context
/// retry loops carry a joint envelope, resolve gaps on failure, and phase
/// C assembles the final <c>CodeGenerationPlan</c>.
/// </summary>
public sealed class RealPlanningAssemblyTests
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
              "dependsOnContextNames": ["Todos"],
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

    private const string NotesCatalogInvalidJson = """
        {
          "boundedContextName": "Notes",
          "contracts": [{
            "name": "NoteContracts",
            "kind": "Contracts",
            "moduleName": "M1",
            "purpose": "Note DTOs.",
            "members": [],
            "capabilityNames": ["ManageNotes"]
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
            "consumesContractNames": ["TodoContracts"],
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

    private const string GuidanceJson = """
        {
          "resolutionKind": "retry",
          "decision": "Assign the Notes contract to module M2.",
          "reasons": ["M1 belongs to the Todos bounded context."],
          "alternativesConsidered": [],
          "consequences": ["Contracts stay inside their owning context."],
          "userOverridable": true,
          "requiresUserInput": false,
          "userQuestion": ""
        }
        """;

    [Fact]
    public async Task Dependent_contexts_assemble_into_a_plan_with_gap_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var promptsRoot = FindPromptsRoot();
        static async Task<byte[]> PackBytes(string root, string pack, string file, CancellationToken ct) =>
            await File.ReadAllBytesAsync(Path.Combine(root, pack, file), ct);

        var templateEngine = new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot));
        var contractBuilder = new ContractDesignPromptBuilder(templateEngine);
        var componentBuilder = new ComponentDesignPromptBuilder(templateEngine);
        var gapBuilder = new PlanningGapResolutionPromptBuilder(templateEngine);
        var repairer = new LlmStructuredOutputRepairer(JsonRepairPipeline.Create());

        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        var optionalStr = new OptionalType(str);
        var optionalJson = new OptionalType(json);
        var list8 = new ListType(json, 8);
        var (attemptSchemaDescriptor, attemptSchema) = PlanningFuwenDescriptors.StageAttemptSchema();
        var attemptType = new NamedTypeReference(attemptSchemaDescriptor);
        var (jointSchemaDescriptor, jointSchema) = PlanningFuwenDescriptors.JointAttemptSchema();
        var jointType = new NamedTypeReference(jointSchemaDescriptor);

        var contractProfile = PlanningFuwenDescriptors.StageProfile("contract-design", Model, 12000);
        var componentProfile = PlanningFuwenDescriptors.StageProfile("component-design", Model, 16000);
        var contractTemplate = PlanningFuwenDescriptors.Template(
            "contract-design", "Guyabano.CodeGeneration.Planning.BoundedContextContractCatalog",
            await PackBytes(promptsRoot, "contract-design", "system.sbn", ct),
            await PackBytes(promptsRoot, "contract-design", "user.sbn", ct));
        var componentTemplate = PlanningFuwenDescriptors.Template(
            "component-design", "Guyabano.CodeGeneration.Planning.BoundedContextComponentManifest",
            await PackBytes(promptsRoot, "component-design", "system.sbn", ct),
            await PackBytes(promptsRoot, "component-design", "user.sbn", ct));
        var gapActivity = PlanningFuwenDescriptors.GapActivity(
            await PackBytes(promptsRoot, "planning-gap-resolution", "system.sbn", ct),
            await PackBytes(promptsRoot, "planning-gap-resolution", "user.sbn", ct));
        var assessActivity = PlanningFuwenDescriptors.AssessActivity();
        var applyActivity = PlanningFuwenDescriptors.ApplyGuidanceActivity();
        var assembleActivity = PlanningFuwenDescriptors.AssembleActivity();

        CallableContract StageContract(FuwenType output, params (string Name, FuwenType Type)[] parameters) => new(
            new CallableSignature([.. parameters.Select(p => new CallableParameter(p.Name, p.Type))], output),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(contractProfile, callableContract:
                StageContract(attemptType, ("bundle", json), ("previousFailure", optionalStr))),
            new TrustedCatalogueDescriptor(componentProfile, callableContract:
                StageContract(attemptType, ("bundle", json), ("catalog", attemptType), ("previousFailure", optionalStr))),
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
        var compiler = new WorkflowCompiler(catalogue,
            capabilityPolicy: new CapabilityGrantPolicy("policy/1", []));

        var router = new PhasedStageRouter();
        var ports = new FuwenZhinuExecutionPorts(
            new StageActivityRouter(
                new AssessContextDesignActivity(),
                new ApplyGuidanceActivity(),
                new ResolveStageGuidanceActivity(router, gapBuilder, repairer, Model, 6000),
                new AssemblePlanningActivity()),
            new UnusedContext(),
            new PhasedInferenceRouter(
                new PlanningContractExecutor(router, contractBuilder, repairer, Model, 12000, outputEnvelope: true),
                new PlanningComponentExecutor(router, componentBuilder, repairer, Model, 16000, outputEnvelope: true)));
        var root = Path.Combine(Path.GetTempPath(), "guyabano-assembly", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            var phaseRunner = new PhaseRunner(
                store, compiler, ports, jointType, attemptType, jointSchema, attemptSchema,
                contractProfile, componentProfile, contractTemplate, componentTemplate,
                gapActivity, assessActivity, applyActivity, assembleActivity);

            // Phase B1: Todos (independent, no upstream).
            var todos = await phaseRunner.RunContextPhaseAsync(
                "todos", BundleJson("Todos", [], []), ct);
            todos.GetProperty("ok").GetBoolean().Should().BeTrue();
            todos.GetProperty("catalog").GetProperty("boundedContextName").GetString().Should().Be("Todos");

            // Phase B2: Notes (depends on Todos; upstream embedded in the
            // bundle literal). First contract attempt targets the wrong
            // module, gap guidance retries it successfully.
            var notes = await phaseRunner.RunContextPhaseAsync(
                "notes",
                BundleJson("Notes", [todos.GetProperty("catalog")], [todos.GetProperty("manifest")]),
                ct);
            notes.GetProperty("ok").GetBoolean().Should().BeTrue();
            notes.GetProperty("manifest").GetProperty("components").EnumerateArray().Single()
                .GetProperty("definesContractNames").EnumerateArray().Single()
                .GetString().Should().Be("NoteContracts");

            // Phase C: assemble the final plan from both contexts.
            var assembly = await phaseRunner.RunAssemblyAsync(
                [todos.GetProperty("catalog"), notes.GetProperty("catalog")],
                [todos.GetProperty("manifest"), notes.GetProperty("manifest")],
                ct);
            var plan = JsonSerializer.Deserialize<CodeGenerationPlan>(assembly.GetRawText());
            plan.Should().NotBeNull();
            plan!.Tasks.Should().HaveCountGreaterThanOrEqualTo(2);
            plan.Tasks.Should().Contain(task => task.Id == "TASK-SCAFFOLD");

            // Provider calls: 2 contracts + 2 manifests + 1 gap guidance,
            // with no wasted manifest call on the failed first Notes
            // contract attempt (skip short-circuit).
            router.RequestCalls.Should().Be(6);
            var markers = router.Requests.Select(RequestMarker).ToArray();
            markers.Where(m => m == "contract-Todos").Should().HaveCount(1);
            markers.Where(m => m == "contract-Notes").Should().HaveCount(2);
            markers.Where(m => m == "component-Todos").Should().HaveCount(1);
            markers.Where(m => m == "component-Notes").Should().HaveCount(1);
            markers.Where(m => m == "gap").Should().HaveCount(1);
        }
        finally
        {
            for (var i = 0; i < 5 && Directory.Exists(root); i++)
            {
                try { Directory.Delete(root, true); break; } catch { Thread.Sleep(50 * (i + 1)); }
            }
        }
    }

    private static string BundleJson(
        string contextName, JsonElement[] upstreamCatalogs, JsonElement[] upstreamManifests)
    {
        using var domain = JsonDocument.Parse(DomainJson);
        using var topology = JsonDocument.Parse(TopologyJson);
        var context = topology.RootElement.GetProperty("boundedContexts").EnumerateArray()
            .First(element => element.GetProperty("name").GetString() == contextName);
        var catalogs = new JsonArray();
        foreach (var element in upstreamCatalogs)
            catalogs.Add(JsonNode.Parse(element.GetRawText()));
        var manifests = new JsonArray();
        foreach (var element in upstreamManifests)
            manifests.Add(JsonNode.Parse(element.GetRawText()));
        var bundle = new JsonObject
        {
            ["context"] = JsonNode.Parse(context.GetRawText()),
            ["domain"] = JsonNode.Parse(domain.RootElement.GetRawText()),
            ["topology"] = JsonNode.Parse(topology.RootElement.GetRawText()),
            ["upstreamCatalogs"] = catalogs,
            ["upstreamManifests"] = manifests,
        };
        return bundle.ToJsonString();
    }

    private sealed class PhaseRunner(
        SqliteWorkflowStore store,
        WorkflowCompiler compiler,
        FuwenZhinuExecutionPorts ports,
        NamedTypeReference jointType,
        NamedTypeReference attemptType,
        ObjectSchemaDefinition jointSchema,
        ObjectSchemaDefinition attemptSchema,
        DescriptorReference contractProfile,
        DescriptorReference componentProfile,
        DescriptorReference contractTemplate,
        DescriptorReference componentTemplate,
        DescriptorReference gapActivity,
        DescriptorReference assessActivity,
        DescriptorReference applyActivity,
        DescriptorReference assembleActivity)
    {
        public async Task<JsonElement> RunContextPhaseAsync(
            string phase, string bundleJson, CancellationToken ct)
        {
            var str = new PrimitiveType(FuwenPrimitiveKind.String);
            var json = new PrimitiveType(FuwenPrimitiveKind.Json);
            var loopPath = StructuralNodeIdentity.Create("phaseB", "design");
            var contractPath = loopPath + "/$body/contract";
            var manifestPath = loopPath + "/$body/manifest";
            var assessPath = loopPath + "/$body/assess";
            var gapPath = loopPath + "/$body/gap";
            var applyPath = loopPath + "/$body/apply";
            var returnPath = StructuralNodeIdentity.Create("phaseB", "return_result");
            using var bundle = JsonDocument.Parse(bundleJson);
            using var initial = JsonDocument.Parse(
                """{"ok":false,"catalog":null,"manifest":null,"error":"","retryStage":null,"retryArtifact":null}""");
            using var breakLiteral = JsonDocument.Parse("true");
            var plan = new WorkflowPlanBuilder("phaseB", "1", str, jointType, "routing/1")
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
                    new WorkflowExecutionRegion("phaseB", [
                        new WorkflowExecutionPhase([loopPath]),
                        new WorkflowExecutionPhase([returnPath]),
                    ]),
                    new WorkflowExecutionRegion("phaseB/design/$body", [
                        new WorkflowExecutionPhase([contractPath]),
                        new WorkflowExecutionPhase([manifestPath]),
                        new WorkflowExecutionPhase([assessPath]),
                        new WorkflowExecutionPhase([gapPath]),
                        new WorkflowExecutionPhase([applyPath]),
                    ]),
                ]))
                .Build();

            var admission = await new WorkflowAdmissionService(compiler)
                .AdmitAsync(plan, cancellationToken: ct);
            admission.Succeeded.Should().BeTrue(
                string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path} expected={d.Expected} actual={d.Actual}")));
            var registration = await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    new FuwenZhinuProviderRuntimeIdentity(
                        admission.Receipt!.CatalogueSnapshotRevision,
                        admission.Receipt.ResolvedDescriptorSetFingerprint),
                    ports)
                .CreateAsync($"fuwen.phase-b-{phase}", "1", admission, ct);
            var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            await using var _ = engine;
            using var input = JsonDocument.Parse(JsonSerializer.Serialize("Plan todos and notes."));
            var runId = await engine.StartAsync($"fuwen.phase-b-{phase}", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);
            output.GetProperty("ok").GetBoolean().Should().BeTrue();
            return output.Clone();
        }

        public async Task<JsonElement> RunAssemblyAsync(
            JsonElement[] catalogs, JsonElement[] manifests, CancellationToken ct)
        {
            var json = new PrimitiveType(FuwenPrimitiveKind.Json);
            var list8 = new ListType(json, 8);
            var assemblePath = StructuralNodeIdentity.Create("phaseC", "assemble");
            var returnPath = StructuralNodeIdentity.Create("phaseC", "return_result");
            static ListBinding ArrayOf(JsonElement[] items) => new(
                items.Select(item => (Binding)new LiteralBinding(item.Clone())).ToArray());
            var plan = new WorkflowPlanBuilder("phaseC", "1", json, json, "routing/1")
                .AddNode(new ActivityNode("assemble", assemblePath, assembleActivity,
                    [
                        new ArgumentBinding("domain", new LiteralBinding(JsonDocument.Parse(DomainJson).RootElement.Clone())),
                        new ArgumentBinding("topology", new LiteralBinding(JsonDocument.Parse(TopologyJson).RootElement.Clone())),
                        new ArgumentBinding("catalogs", ArrayOf(catalogs)),
                        new ArgumentBinding("manifests", ArrayOf(manifests)),
                    ], json))
                .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(assemblePath, [])))
                .SetExecutionOrder(new WorkflowExecutionOrder([
                    new WorkflowExecutionRegion("phaseC", [
                        new WorkflowExecutionPhase([assemblePath]),
                        new WorkflowExecutionPhase([returnPath]),
                    ]),
                ]))
                .Build();
            var admission = await new WorkflowAdmissionService(compiler)
                .AdmitAsync(plan, cancellationToken: ct);
            admission.Succeeded.Should().BeTrue(
                string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
            var registration = await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    new FuwenZhinuProviderRuntimeIdentity(
                        admission.Receipt!.CatalogueSnapshotRevision,
                        admission.Receipt.ResolvedDescriptorSetFingerprint),
                    ports)
                .CreateAsync("fuwen.phase-c", "1", admission, ct);
            var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            await using var _ = engine;
            using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { catalogs, manifests }));
            var runId = await engine.StartAsync("fuwen.phase-c", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            return (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).Clone();
        }
    }

    private static string RequestMarker(LlmRequest request)
    {
        var text = string.Concat(request.Messages
            .SelectMany(m => m.Parts)
            .OfType<LlmTextContent>()
            .Select(p => p.Text));
        if (text.Contains("Owning planning stage:", StringComparison.Ordinal))
            return "gap";
        if (text.Contains("Design components for this bounded context:", StringComparison.Ordinal))
            return "component-" + ContextAfter(text, "Design components for this bounded context:");
        if (text.Contains("Design contracts for this bounded context:", StringComparison.Ordinal))
            return "contract-" + ContextAfter(text, "Design contracts for this bounded context:");
        return "other";
    }

    private static string ContextAfter(string text, string marker)
    {
        // Scope to the context block: the first "name" after the marker is
        // the target context. A whole-tail search would match upstream
        // artifact names (e.g. the embedded Todos catalog in a Notes prompt).
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

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class StageActivityRouter(
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

    private sealed class PhasedInferenceRouter(
        PlanningContractExecutor contract,
        PlanningComponentExecutor component) : IInferenceExecutor, IInferenceExecutorPreflight
    {
        public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement) =>
            requirement.PromptTemplate?.Name switch
            {
                var name when name == PlanningFuwenDescriptors.TemplateName("contract-design") => contract.Preflight(requirement),
                var name when name == PlanningFuwenDescriptors.TemplateName("component-design") => component.Preflight(requirement),
                _ => null,
            };

        public async ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken ct = default)
        {
            if (request.PromptTemplate is null)
            {
                throw new InvalidOperationException(
                    "The staged planning test router serves template-driven inference only.");
            }
            var template = request.PromptTemplate.Name switch
            {
                var name when name == PlanningFuwenDescriptors.TemplateName("contract-design") => (IInferenceExecutor)contract,
                var name when name == PlanningFuwenDescriptors.TemplateName("component-design") => component,
                _ => throw new InvalidOperationException($"Unknown prompt template '{request.PromptTemplate.Name}'."),
            };
            return await template.ExecuteAsync(request, ct);
        }
    }

    private sealed class PhasedStageRouter : ILlmRouter
    {
        private readonly Dictionary<string, Queue<string>> queues = new(StringComparer.Ordinal)
        {
            ["contract-Todos"] = new([TodosCatalogJson]),
            ["contract-Notes"] = new([NotesCatalogInvalidJson, NotesCatalogJson]),
            ["component-Todos"] = new([TodosManifestJson]),
            ["component-Notes"] = new([NotesManifestJson]),
            ["gap"] = new([GuidanceJson]),
        };

        public List<LlmRequest> Requests { get; } = [];
        public int RequestCalls { get; private set; }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            RequestCalls++;
            Requests.Add(request);
            var key = RequestMarker(request);
            if (!queues.TryGetValue(key, out var queue) || queue.Count == 0)
                throw new InvalidOperationException($"No scripted response for stage '{key}'.");
            return StreamSingle(queue.Dequeue());
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
