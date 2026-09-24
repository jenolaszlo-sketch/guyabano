using System.Text.Json;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Guyabano.CodeGeneration.Workflows.FuwenPilot;

/// <summary>
/// Pilot that reproduces Guyabano's hard-coded <see cref="CodeGenerationWorkflow"/>
/// via Fuwen IR v3-v7. Exercises all Fuwen features in a single coherent pipeline:
///   v3: context, infer, activity, return (sequential spine)
///   v5: conditional merge (value-producing branch on review need)
///   v6: repeat loop (bounded architecture review passes)
///   v4: fan-out keyed decomposition wave (generation wave)
///   v7: checkpoint (durable state persistence)
///   v7: wait (external approval gate / signal suspend)
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

    public static readonly DescriptorReference GenerateDescriptor = new(
        DescriptorKind.Activity, "guyabano.generate", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('f', 64)));

    public static readonly DescriptorReference ReviewDescriptor = new(
        DescriptorKind.Activity, "guyabano.review", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('0', 64)));

    public static readonly DescriptorReference PrepareTasksDescriptor = new(
        DescriptorKind.Activity, "guyabano.prepare-tasks", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('2', 64)));

    private static ContentDigest Digest(char c) => new("sha256", "descriptor/v1", new string(c, 64));

    private static PrimitiveType Str => new(FuwenPrimitiveKind.String);

    public static ITrustedCatalogue CreateCatalogue() => new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(ContextDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("request", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(PlanProfile, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("request", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(PlanTemplate),
        new TrustedCatalogueDescriptor(ScaffoldDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("plan", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(BuildDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("artifact", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(GenerateDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("task", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(ReviewDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("value", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(PrepareTasksDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("plan", Str)],
                new ListType(Str, 8)),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    ]);

    /// <summary>Compile the DSL file codegen-pilot.fuwen and return the admitted plan.</summary>
    public static async Task<WorkflowAdmissionResult> CompileDslAsync(CancellationToken ct = default)
    {
        var source = await ReadDslSourceAsync(ct).ConfigureAwait(false);
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

    internal static async Task<string> ReadDslSourceAsync(CancellationToken ct = default)
    {
        var source = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "FuwenPilot", "codegen-pilot.fuwen"), ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(source) || !source.Contains("workflow codegenPilot"))
        {
            var alt = Path.Combine(
                Path.GetDirectoryName(typeof(CodegenFuwenPilot).Assembly.Location)!,
                "..", "..", "..", "..", "src", "Guyabano.CodeGeneration.Workflows", "FuwenPilot", "codegen-pilot.fuwen");
            if (File.Exists(alt))
                source = await File.ReadAllTextAsync(alt, ct).ConfigureAwait(false);
        }

        return source;
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
            .Build();
    }

    /// <summary>
    /// Programmatic plan with IR v4 keyed fan-out mirroring Guyabano's generation wave.
    /// </summary>
    public static WorkflowPlan BuildFanOutPlan()
    {
        var input = new ListType(Str, 8);
        var itemType = Str;
        var output = new ListType(Str, 8);

        var fanOutPath = StructuralNodeIdentity.Create("codegenFanOut", "generate");
        var returnPath = StructuralNodeIdentity.Create("codegenFanOut", "return_result");

        var bodyActivityPath = $"{fanOutPath}/$body/gen";
        var fanOut = new FanOutNode(
            "generate",
            fanOutPath,
            new InputBinding([]),
            new FanOutItemBinding("item", itemType),
            new FanOutItemValueBinding([]),
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
            .Build();
    }

    /// <summary>
    /// Extended programmatic plan exercising IR v3-v7:
    ///   v3: ctx -> plan (sequential spine)
    ///   v5: conditional merge (review branch)
    ///   v6: repeat loop (bounded review passes, seeded from workflow input
    ///       because closed-region rules confine repeat seeds to region-free bindings)
    ///   v4: fan-out keyed decomposition wave
    ///   v7: checkpoint + wait (durable state + approval gate)
    /// Mirrors codegen-pilot.fuwen.
    /// </summary>
    public static WorkflowPlan BuildExtendedPlan()
    {
        var list8 = new ListType(Str, 8);

        var ctxPath = StructuralNodeIdentity.Create("codegenPilot", "ctx");
        var planPath = StructuralNodeIdentity.Create("codegenPilot", "plan");

        // v5: conditional merge
        var condPath = StructuralNodeIdentity.Create("codegenPilot", "needsReview");
        var reviewActPath = condPath + "/$then/reviewActivity";
        var skipActPath = condPath + "/$else/skipReview";

        // v6: repeat loop
        var loopPath = StructuralNodeIdentity.Create("codegenPilot", "reviewLoop");
        var loopBodyPath = loopPath + "/$body/reviewPass";

        // prepare tasks activity
        var preparePath = StructuralNodeIdentity.Create("codegenPilot", "prepareTasks");

        // v4: fan-out
        var fanOutPath = StructuralNodeIdentity.Create("codegenPilot", "tasks");
        var fanOutBodyPath = fanOutPath + "/$body/generate";

        // v7: checkpoint + wait + build
        var checkpointPath = StructuralNodeIdentity.Create("codegenPilot", "generated");
        var waitPath = StructuralNodeIdentity.Create("codegenPilot", "approval");
        var buildPath = StructuralNodeIdentity.Create("codegenPilot", "build");
        var returnPath = StructuralNodeIdentity.Create("codegenPilot", "return_result");

        var loopReviewArgs = new List<ArgumentBinding> { new("value", new LoopStateBinding([])) };
        // Branch bodies are closed regions: they only accept region-free
        // bindings (workflow input / literals), matching Fuwen's own
        // conditional-merge fixtures. The branch condition itself lives in
        // the parent region and may read the plan output.
        var reviewArgs = new List<ArgumentBinding> { new("value", new InputBinding([])) };

        var plan = new WorkflowPlanBuilder("codegenPilot", "1", Str, Str, "routing/1")
            .AddNode(new ContextNode("ctx", ctxPath, ContextDescriptor,
                [new ArgumentBinding("request", new InputBinding([]))], Str))
            .AddNode(new InferenceNode("plan", planPath, PlanProfile, PlanTemplate,
                [new ArgumentBinding("request", new NodeOutputBinding(ctxPath, []))], [], Str,
                [new ContextRequirement("ctx", new NodeOutputBinding(ctxPath, []), Str)]))
            // v5: conditional merge
            .AddNode(new ConditionalNode(
                "needsReview", condPath,
                new ConditionExpression(ConditionOperator.NotEqual,
                    new NodeOutputBinding(planPath, []),
                    new LiteralBinding(JsonDocument.Parse("\"\"").RootElement.Clone())),
                [new ActivityNode("reviewActivity", reviewActPath, ReviewDescriptor, reviewArgs, Str)],
                [new ActivityNode("skipReview", skipActPath, ReviewDescriptor, reviewArgs, Str)],
                new ConditionalMerge(
                    new NodeOutputBinding(reviewActPath, []),
                    new NodeOutputBinding(skipActPath, []),
                    Str)))
            // v6: repeat loop (seeded from workflow input: repeat regions
            // only accept region-free initial-state bindings)
            .AddNode(new RepeatNode(
                "reviewLoop", loopPath,
                3, Str,
                new InputBinding([]),
                [new ActivityNode("reviewPass", loopBodyPath, ReviewDescriptor, loopReviewArgs, Str)],
                new NodeOutputBinding(loopBodyPath, []),
                new ConditionExpression(ConditionOperator.Equal,
                    new LoopIterationBinding([]),
                    new LiteralBinding(JsonDocument.Parse("3").RootElement.Clone())),
                Str))
            // activity to produce task list
            .AddNode(new ActivityNode("prepareTasks", preparePath, PrepareTasksDescriptor,
                [new ArgumentBinding("plan", new NodeOutputBinding(loopPath, []))], list8))
            // v4: fan-out
            .AddFanOut(new FanOutNode(
                "tasks", fanOutPath,
                new NodeOutputBinding(preparePath, []),
                new FanOutItemBinding("task", Str),
                new FanOutItemValueBinding([]),
                [new ActivityNode("generate", fanOutBodyPath, GenerateDescriptor,
                    [new ArgumentBinding("task", new FanOutItemValueBinding([]))], Str)],
                new NodeOutputBinding(fanOutBodyPath, []),
                list8,
                MaximumItems: 8,
                MaximumConcurrency: 4))
            // v7: checkpoint
            .AddNode(new CheckpointNode("generated", checkpointPath,
                new NodeOutputBinding(fanOutPath, []), list8))
            // v7: wait
            .AddNode(new WaitNode("approval", waitPath, "approval-signal", Str,
                TimeoutSeconds: 300))
            // build
            .AddNode(new ActivityNode("build", buildPath, BuildDescriptor,
                [new ArgumentBinding("artifact", new NodeOutputBinding(waitPath, []))], Str))
            // return
            .AddNode(new ReturnNode("return_result", returnPath,
                new NodeOutputBinding(buildPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("codegenPilot", [
                    new WorkflowExecutionPhase([ctxPath]),
                    new WorkflowExecutionPhase([planPath]),
                    new WorkflowExecutionPhase([condPath]),
                    new WorkflowExecutionPhase([loopPath]),
                    new WorkflowExecutionPhase([preparePath]),
                    new WorkflowExecutionPhase([fanOutPath]),
                    new WorkflowExecutionPhase([checkpointPath]),
                    new WorkflowExecutionPhase([waitPath]),
                    new WorkflowExecutionPhase([buildPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{condPath}/$then", [
                    new WorkflowExecutionPhase([reviewActPath]),
                ]),
                new WorkflowExecutionRegion($"{condPath}/$else", [
                    new WorkflowExecutionPhase([skipActPath]),
                ]),
                new WorkflowExecutionRegion($"{loopPath}/$body", [
                    new WorkflowExecutionPhase([loopBodyPath]),
                ]),
                new WorkflowExecutionRegion($"{fanOutPath}/$body", [
                    new WorkflowExecutionPhase([fanOutBodyPath]),
                ]),
            ]));

        return plan.Build();
    }

    public static async Task<WorkflowAdmissionResult> AdmitAsync(WorkflowPlan plan, CancellationToken ct = default)
    {
        var admission = await new WorkflowAdmissionService(
            new WorkflowCompiler(CreateCatalogue(), capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct).ConfigureAwait(false);
        if (!admission.Succeeded)
            throw new InvalidOperationException($"Admission failed: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path} expected={d.Expected} actual={d.Actual}"))}");
        return admission;
    }

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
