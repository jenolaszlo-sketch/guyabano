#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// The web /plan catalogue declares the full planning-context contract with
/// omittable options, and /run executes an admitted domain plan with the real
/// context provider defaults plus a stubbed inference step. No model call.
/// </summary>
public sealed class DomainPlanExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-domain-plan-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Minimal_context_node_admits_and_executes_with_provider_defaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var optionalStr = new OptionalType(str);
        var optionalBoolean = new OptionalType(new PrimitiveType(FuwenPrimitiveKind.Boolean));
        var optionalInteger = new OptionalType(new PrimitiveType(FuwenPrimitiveKind.Integer));
        var context = PlanningFuwenDescriptors.Context();
        var profile = PlanningFuwenDescriptors.Profile("stub-model", 8000);
        var template = new DescriptorReference(
            DescriptorKind.PromptTemplate,
            "stub.domain-template",
            "1",
            new ContentDigest("sha256", "descriptor/v1", new string('7', 64)));
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(context, callableContract: new CallableContract(
                new CallableSignature(
                    [
                        new CallableParameter("request", str),
                        new CallableParameter("repositoryContext", optionalStr),
                        new CallableParameter("includeRepositoryContext", optionalBoolean),
                        new CallableParameter("maxCharacters", optionalInteger),
                    ],
                    str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(profile, callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("request", str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(template),
        ]);

        // The model may omit every optional context argument; the provider
        // defaults apply and the plan still admits.
        var dsl = $$"""
            workflow plan(input: string) -> string {
              context ctx = context "{{context.Name}}@{{context.Version}}#{{context.ContentDigest.Value}}" (request: input;) -> string;
              infer discovery = infer "{{profile.Name}}@{{profile.Version}}#{{profile.ContentDigest.Value}}" using "{{template.Name}}@{{template.Version}}#{{template.ContentDigest.Value}}" (request: ctx;) -> string;
              return discovery;
            }
            """;
        var compiled = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(dsl, cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(
            string.Join("; ", compiled.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
        var admission = await new WorkflowAdmissionService(
                new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(compiled.Plan!, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));

        Directory.CreateDirectory(_root);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(_root, "workflow.db"),
            Pooling = false,
        });
        var ports = new FuwenZhinuExecutionPorts(
            new UnusedActivity(),
            new PlanningRequestContextProvider(),
            new StubInference("{\"title\":\"stub domain\"}"));
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                ports)
            .CreateAsync("plan", "1", admission, ct);
        await using var engine = new WorkflowEngine(
            store,
            registration.Register(new WorkflowRegistry()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
        using var input = JsonDocument.Parse("\"A todo list.\"");
        var runId = await engine.StartAsync(
            "plan", "1", input.RootElement.Clone(), cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

        // The context provider defaulted the omitted options (no repository
        // block), and the stubbed inference result flowed to the return.
        output.GetString().Should().Contain("stub domain");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            for (var i = 0; i < 5; i++)
            {
                try { Directory.Delete(_root, true); break; }
                catch { Thread.Sleep(50 * (i + 1)); }
            }
        }
    }

    private sealed class UnusedActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class StubInference(string json) : IInferenceExecutor, IInferenceExecutorPreflight
    {
        public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement) => null;

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request, CancellationToken ct = default)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(json));
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(
                RuntimeValue.FromJson(document.RootElement.Clone())));
        }
    }
}
