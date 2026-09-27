using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Delivery F Wave 2: generation wave and review branching parity.
///
/// The generation wave is inference-driven, so this wave locks the token-use
/// parity dimension Wave 1a deferred: scripted inference providers report
/// token evidence, and the corpus asserts exact per-item and retry-inclusive
/// totals. Review branching proves verdict-driven accept/repair selection
/// with divergent downstream paths through the v5 conditional merge.
/// </summary>
public sealed class GenerationReviewParityTests
{
    [Fact]
    public async Task InferenceFanOut_AccountsTokensPerItem()
    {
        var ct = TestContext.Current.CancellationToken;
        var inference = new ScriptedInference();
        await using var run = await ParityRun.StartAsync(BuildGenerationPlan(), GenerationCatalogue(), GenerationPorts(inference), "parity.generate", "[\"a\",\"b\"]", ct);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var output = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);

        output.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("generated:a", "generated:b");
        inference.Calls.Should().Be(2);
        inference.PromptTokens.Should().Be(20);
        inference.CompletionTokens.Should().Be(10);
    }

    [Fact]
    public async Task InferenceFailOnce_AccountsRetryTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var inference = new ScriptedInference();
        inference.Script("a",
            ScriptedInferenceOutcome.TransientFailure(),
            ScriptedInferenceOutcome.Success());
        await using var run = await ParityRun.StartAsync(BuildGenerationPlan(), GenerationCatalogue(), GenerationPorts(inference, 2), "parity.generate", "[\"a\",\"b\"]", ct);

        await run.Engine.ExecuteAsync(run.RunId, ct);
        var output = await run.Engine.WaitForCompletionAsync<JsonElement>(run.RunId, cancellationToken: ct);

        output.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("generated:a", "generated:b");
        // Both attempts cost tokens: the failed attempt is visible in the total.
        inference.Calls.Should().Be(3);
        inference.PromptTokens.Should().Be(30);
        inference.CompletionTokens.Should().Be(15);
    }

    [Fact]
    public async Task ReviewAccept_SelectsAcceptPath()
    {
        var (output, calls) = await RunReviewAsync("case-accept");

        output.Should().Be("accepted");
        calls.Should().Contain("parity.accept");
        calls.Should().NotContain("parity.repair");
    }

    [Fact]
    public async Task ReviewRepair_SelectsRepairPath()
    {
        var (output, calls) = await RunReviewAsync("case-repair");

        output.Should().Be("repaired");
        calls.Should().Contain("parity.repair");
        calls.Should().NotContain("parity.accept");
    }

    private static async Task<(string Output, List<string> Calls)> RunReviewAsync(string input)
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new ScriptedReviewActivity();
        var plan = BuildReviewPlan();
        var catalogue = ReviewCatalogue();
        var admission = await new WorkflowAdmissionService(
                new WorkflowCompiler(catalogue,
                    capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Succeeded)
            throw new InvalidOperationException(
                $"Admission failed: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path} expected={d.Expected} actual={d.Actual}"))}");
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("parity.review", "1", admission, ct)
            .ConfigureAwait(false);
        var root = Path.Combine(
            Path.GetTempPath(), "guyabano-review-parity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(store,
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var inputJson = JsonDocument.Parse(JsonSerializer.Serialize(input));
            var runId = await engine.StartAsync("parity.review", "1", inputJson.RootElement.Clone(),
                cancellationToken: ct).ConfigureAwait(false);
            await engine.ExecuteAsync(runId, ct).ConfigureAwait(false);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)
                .ConfigureAwait(false);
            return (output.GetString()!, activity.Calls);
        }
        finally
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch { await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false); }
            }
        }
    }

    private static readonly DescriptorReference GenerateProfile = new(
        DescriptorKind.InferenceProfile, "parity.generate-profile", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('e', 64)));

    private static readonly DescriptorReference GenerateTemplate = new(
        DescriptorKind.PromptTemplate, "parity.generate-template", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('d', 64)));

    private static readonly DescriptorReference ReviewDescriptor = new(
        DescriptorKind.Activity, "parity.review", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('b', 64)));

    private static readonly DescriptorReference AcceptDescriptor = new(
        DescriptorKind.Activity, "parity.accept", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));

    private static readonly DescriptorReference RepairDescriptor = new(
        DescriptorKind.Activity, "parity.repair", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('c', 64)));

    private static PrimitiveType Str => new(FuwenPrimitiveKind.String);

    private static ITrustedCatalogue GenerationCatalogue() => new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(GenerateProfile, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("request", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(GenerateTemplate),
    ]);

    private static ITrustedCatalogue ReviewCatalogue() => new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(ReviewDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("value", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(AcceptDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("value", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(RepairDescriptor, callableContract: new CallableContract(
            new CallableSignature([new CallableParameter("value", Str)], Str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
    ]);

    private static WorkflowPlan BuildGenerationPlan()
    {
        var itemType = Str;
        var output = new ListType(itemType, 8);
        var fanOutPath = StructuralNodeIdentity.Create("parityGenerate", "generate");
        var returnPath = StructuralNodeIdentity.Create("parityGenerate", "return_result");
        var bodyInferencePath = $"{fanOutPath}/$body/gen";
        var fanOut = new FanOutNode(
            "generate",
            fanOutPath,
            new InputBinding([]),
            new FanOutItemBinding("item", itemType),
            new FanOutItemValueBinding([]),
            new List<WorkflowNode>
            {
                new InferenceNode(
                    "gen",
                    bodyInferencePath,
                    GenerateProfile,
                    GenerateTemplate,
                    [new ArgumentBinding("request", new FanOutItemValueBinding([]))],
                    [],
                    itemType,
                    ContextRequirements: [])
            },
            new NodeOutputBinding(bodyInferencePath, []),
            output,
            MaximumItems: 8,
            MaximumConcurrency: 4);
        return new WorkflowPlanBuilder("parityGenerate", "1", new ListType(itemType, 8), output, "routing/1")
            .AddFanOut(fanOut)
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(fanOutPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("parityGenerate", [
                    new WorkflowExecutionPhase([fanOutPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{fanOutPath}/$body", [
                    new WorkflowExecutionPhase([bodyInferencePath]),
                ]),
            ]))
            .Build();
    }

    private static WorkflowPlan BuildReviewPlan()
    {
        var reviewPath = StructuralNodeIdentity.Create("parityReview", "review");
        var condPath = StructuralNodeIdentity.Create("parityReview", "decision");
        var acceptPath = condPath + "/$then/accept";
        var repairPath = condPath + "/$else/repair";
        var returnPath = StructuralNodeIdentity.Create("parityReview", "return_result");
        return new WorkflowPlanBuilder("parityReview", "1", Str, Str, "routing/1")
            .AddNode(new ActivityNode("review", reviewPath, ReviewDescriptor,
                [new ArgumentBinding("value", new InputBinding([]))], Str))
            .AddNode(new ConditionalNode(
                "decision", condPath,
                new ConditionExpression(ConditionOperator.Equal,
                    new NodeOutputBinding(reviewPath, []),
                    new LiteralBinding(JsonDocument.Parse("\"accept\"").RootElement.Clone())),
                [new ActivityNode("accept", acceptPath, AcceptDescriptor,
                    [new ArgumentBinding("value", new InputBinding([]))], Str)],
                [new ActivityNode("repair", repairPath, RepairDescriptor,
                    [new ArgumentBinding("value", new InputBinding([]))], Str)],
                new ConditionalMerge(
                    new NodeOutputBinding(acceptPath, []),
                    new NodeOutputBinding(repairPath, []),
                    Str)))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(condPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("parityReview", [
                    new WorkflowExecutionPhase([reviewPath]),
                    new WorkflowExecutionPhase([condPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{condPath}/$then", [
                    new WorkflowExecutionPhase([acceptPath]),
                ]),
                new WorkflowExecutionRegion($"{condPath}/$else", [
                    new WorkflowExecutionPhase([repairPath]),
                ]),
            ]))
            .Build();
    }

    private static FuwenZhinuExecutionPorts GenerationPorts(
        IInferenceExecutor inference, int maximumInfrastructureAttempts = 1) =>
        new(new UnusedActivity(), new UnusedContext(), inference,
            observer: null,
            new FuwenZhinuExecutionPorts.Options(
                maximumInfrastructureAttempts, maximumFanOutConcurrency: 2));

    private sealed class ScriptedInference : IInferenceExecutor, IInferenceExecutorPreflight
    {
        private readonly Dictionary<string, Queue<ScriptedInferenceOutcome>> _scripts = new(StringComparer.Ordinal);
        public int Calls;
        public int PromptTokens;
        public int CompletionTokens;

        public void Script(string input, params ScriptedInferenceOutcome[] outcomes) =>
            _scripts[input] = new Queue<ScriptedInferenceOutcome>(outcomes);

        internal static InferenceExecutionEvidence Evidence() =>
            new(GenerateProfile, GenerateTemplate, [],
                promptTokens: 10, completionTokens: 5, totalTokens: 15);

        public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement) => null;

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request, CancellationToken ct = default)
        {
            var input = request.Arguments.FirstOrDefault()?.Value
                ?? request.ContextInputs.FirstOrDefault()?.Value!;
            var text = input is JsonRuntimeValue json ? json.Value.GetString()! : input.ToString()!;
            Calls++;
            PromptTokens += 10;
            CompletionTokens += 5;
            if (_scripts.TryGetValue(text, out var queue) && queue.Count > 0)
                return ValueTask.FromResult(queue.Dequeue().Apply(text));
            return ValueTask.FromResult(ScriptedInferenceOutcome.Success().Apply(text));
        }
    }

    private sealed record ScriptedInferenceOutcome(bool Succeed)
    {
        public static ScriptedInferenceOutcome Success() => new(true);
        public static ScriptedInferenceOutcome TransientFailure() => new(false);

        public InferenceExecutionResult Apply(string text)
        {
            if (Succeed)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize("generated:" + text));
                return InferenceExecutionResult.Succeeded(
                    RuntimeValue.FromJson(doc.RootElement),
                    evidence: ScriptedInference.Evidence());
            }
            return InferenceExecutionResult.Failed(
                new ExecutionFailure(
                    ExecutionFailureKind.Infrastructure,
                    ExecutionFailureCode.TransientInfrastructureFailure,
                    "boom",
                    ExecutionRetryDisposition.InfrastructureOnly),
                ScriptedInference.Evidence());
        }
    }

    private sealed class ScriptedReviewActivity : IActivityExecutor
    {
        public readonly List<string> Calls = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default)
        {
            Calls.Add(request.Activity.Name);
            var value = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            string output = request.Activity.Name switch
            {
                "parity.review" => value.Contains("accept", StringComparison.Ordinal) ? "accept" : "repair",
                "parity.accept" => "accepted",
                "parity.repair" => "repaired",
                _ => throw new InvalidOperationException($"Unexpected activity '{request.Activity.Name}'."),
            };
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return ValueTask.FromResult(
                ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }
}
