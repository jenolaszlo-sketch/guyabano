using System.Text.Json;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Guyabano.CodeGeneration.Workflows.FuwenPilot;

/// <summary>
/// Pilot that reproduces Guyabano's hard-coded <see cref="CodeGenerationWorkflow"/>
/// sequential spine via Fuwen IR. The hard-coded flow does:
///   operation/start -> repository/index/select/capture -> planning -> architecture review loop
///   -> decomposition wave fan-out -> scaffolding -> generation wave fan-out -> checkpoint -> build/repair -> reindex
/// The DSL text surface currently supports sequential <c>context/infer/activity/conditional/return</c>
/// plus bounded <c>if/else</c>. Loops and keyed fan-out are IR v4 programmatic only
/// (see Penghou.Fuwen docs/keyed-fanout.md). This pilot therefore:
/// 1) compiles a minimal .fuwen file for the linear spine (codegen-pilot.fuwen)
/// 2) builds an equivalent programmatic plan including one FanOut for generation wave
/// Both plans are admitted and executed via Penghou.Fuwen.Zhinu sequential adapter
/// to prove parity with the hard-coded Task.WhenAll waves.
/// </summary>
public static class CodegenFuwenPilot
{
    public static readonly DescriptorReference ContextDescriptor = new(
        DescriptorKind.ContextProvider, "guyabano.context", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));

    public static readonly DescriptorReference PlanProfile = new(
        DescriptorKind.InferenceProfile, "guyabano.plan-profile", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('b', 64)));

    public static readonly DescriptorReference PlanTemplate = new(
        DescriptorKind.PromptTemplate, "guyabano.plan-template", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('c', 64)));

    public static readonly DescriptorReference ScaffoldDescriptor = new(
        DescriptorKind.Activity, "guyabano.scaffold", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('d', 64)));

    public static readonly DescriptorReference BuildDescriptor = new(
        DescriptorKind.Activity, "guyabano.build", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('e', 64)));

    // Generation fan-out descriptors (IR v4)
    public static readonly DescriptorReference GenerateDescriptor = new(
        DescriptorKind.Activity, "guyabano.generate", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('f', 64)));

    private static ContentDigest Digest(char c) => new("sha256", "descriptor/v1", new string(c, 64));

    public static ITrustedCatalogue CreateCatalogue() => new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(ContextDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("request", new PrimitiveType(FuwenPrimitiveKind.String))],
                new PrimitiveType(FuwenPrimitiveKind.String)),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(PlanProfile, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("request", new PrimitiveType(FuwenPrimitiveKind.String))],
                new PrimitiveType(FuwenPrimitiveKind.String)),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(PlanTemplate),
        new TrustedCatalogueDescriptor(ScaffoldDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("plan", new PrimitiveType(FuwenPrimitiveKind.String))],
                new PrimitiveType(FuwenPrimitiveKind.String)),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(BuildDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("artifact", new PrimitiveType(FuwenPrimitiveKind.String))],
                new PrimitiveType(FuwenPrimitiveKind.String)),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(GenerateDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("task", new PrimitiveType(FuwenPrimitiveKind.String))],
                new PrimitiveType(FuwenPrimitiveKind.String)),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    ]);

    /// <summary>Compile the DSL file codegen-pilot.fuwen and return the admitted plan.</summary>
    public static async Task<WorkflowAdmissionResult> CompileDslAsync(CancellationToken ct = default)
    {
        var source = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "FuwenPilot", "codegen-pilot.fuwen"), ct)
            .ConfigureAwait(false);

        // Fallback for test runs where BaseDirectory differs
        if (string.IsNullOrWhiteSpace(source) || !source.Contains("workflow codegenPilot"))
        {
            var alt = Path.Combine(
                Path.GetDirectoryName(typeof(CodegenFuwenPilot).Assembly.Location)!,
                "..", "..", "..", "..", "src", "Guyabano.CodeGeneration.Workflows", "FuwenPilot", "codegen-pilot.fuwen");
            if (File.Exists(alt))
                source = await File.ReadAllTextAsync(alt, ct).ConfigureAwait(false);
        }

        var compiler = new FuwenSourceCompiler(CreateCatalogue());
        var compile = await compiler.CompileAsync(source, cancellationToken: ct).ConfigureAwait(false);
        if (!compile.Succeeded)
            throw new InvalidOperationException($"DSL compile failed: {string.Join("; ", compile.Diagnostics.Select(d => $"{d.Code}:{d.Message}"))}");

        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(CreateCatalogue(),
            capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(compile.Plan!, cancellationToken: ct).ConfigureAwait(false);
        if (!admission.Succeeded)
            throw new InvalidOperationException($"DSL admission failed: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message}"))}");
        return admission;
    }

    /// <summary>
    /// Programmatic sequential plan (no fan-out) — byte-identical to DSL compile
    /// for the linear spine. Useful to prove the old hard-coded sequential chain
    /// maps cleanly.
    /// </summary>
    public static WorkflowPlan BuildSequentialPlan()
    {
        var input = new PrimitiveType(FuwenPrimitiveKind.String);
        var output = new PrimitiveType(FuwenPrimitiveKind.String);
        var ctxPath = StructuralNodeIdentity.Create("codegenPilot", "ctx");
        var planPath = StructuralNodeIdentity.Create("codegenPilot", "plan");
        var scaffoldPath = StructuralNodeIdentity.Create("codegenPilot", "scaffold");
        var buildPath = StructuralNodeIdentity.Create("codegenPilot", "build");
        var returnPath = StructuralNodeIdentity.Create("codegenPilot", "return_result");

        return new WorkflowPlanBuilder("codegenPilot", "1", input, output, "routing/1")
            .AddNode(new ContextNode("ctx", ctxPath, ContextDescriptor,
                [new ArgumentBinding("request", new InputBinding([]))], input))
            .AddNode(new InferenceNode("plan", planPath, PlanProfile, PlanTemplate,
                [new ArgumentBinding("request", new NodeOutputBinding(ctxPath, []))], [], output,
                [new ContextRequirement("ctx", new NodeOutputBinding(ctxPath, []), input)]))
            .AddNode(new ActivityNode("scaffold", scaffoldPath, ScaffoldDescriptor,
                [new ArgumentBinding("plan", new NodeOutputBinding(planPath, []))], output))
            .AddNode(new ActivityNode("build", buildPath, BuildDescriptor,
                [new ArgumentBinding("artifact", new NodeOutputBinding(scaffoldPath, []))], output))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(buildPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("codegenPilot", [
                    new WorkflowExecutionPhase([ctxPath]),
                    new WorkflowExecutionPhase([planPath]),
                    new WorkflowExecutionPhase([scaffoldPath]),
                    new WorkflowExecutionPhase([buildPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ])
            ]))
            .BuildV3();
    }

    /// <summary>
    /// Programmatic plan with IR v4 keyed fan-out mirroring Guyabano's generation wave:
    ///   generation/{ParentId}/{LeafId} via Task.WhenAll in CodeGenerationWorkflow.cs:529
    /// Hard-coded flow computes readyParents/readyLeaves dynamically; Fuwen requires
    /// a bounded list source + key + yield before execution (key uniqueness checked).
    /// </summary>
    public static WorkflowPlan BuildFanOutPlan()
    {
        var input = new ListType(new PrimitiveType(FuwenPrimitiveKind.String), 8);
        var itemType = new PrimitiveType(FuwenPrimitiveKind.String);
        var output = new ListType(new PrimitiveType(FuwenPrimitiveKind.String), 8);

        var fanOutPath = StructuralNodeIdentity.Create("codegenFanOut", "generate");
        var returnPath = StructuralNodeIdentity.Create("codegenFanOut", "return_result");

        // Body: single activity per item that echoes the item
        var bodyActivityPath = $"{fanOutPath}/$body/gen";
        var fanOut = new FanOutNode(
            "generate",
            fanOutPath,
            new InputBinding([]), // source = workflow input list
            new FanOutItemBinding("item", itemType),
            new FanOutItemValueBinding([]), // key = item identity
            new List<WorkflowNode>
            {
                new ActivityNode("gen", bodyActivityPath, GenerateDescriptor,
                    [new ArgumentBinding("task", new FanOutItemValueBinding([]))], itemType)
            },
            new NodeOutputBinding(bodyActivityPath, []),
            output,
            MaximumItems: 8,
            MaximumConcurrency: 4);

        return new WorkflowPlanBuilder("codegenFanOut", "1", input, output, "routing/1")
            .AddFanOut(fanOut)
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(fanOutPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("codegenFanOut", [
                    new WorkflowExecutionPhase([fanOutPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{fanOutPath}/$body", [
                    new WorkflowExecutionPhase([bodyActivityPath]),
                ]),
            ]))
            .BuildV4();
    }

    public static async Task<WorkflowAdmissionResult> AdmitAsync(WorkflowPlan plan, CancellationToken ct = default)
    {
        var admission = await new WorkflowAdmissionService(
            new WorkflowCompiler(CreateCatalogue(), capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct).ConfigureAwait(false);
        if (!admission.Succeeded)
            throw new InvalidOperationException($"Admission failed: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message}"))}");
        return admission;
    }

    /// <summary>Deterministic Json helper for Zhinu RunAsync&lt;JsonElement,JsonElement&gt;.</summary>
    public static JsonElement ToJson(string value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }

    public static JsonElement ToJsonArray(IEnumerable<string> values)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(values.ToArray()));
        return doc.RootElement.Clone();
    }
}
