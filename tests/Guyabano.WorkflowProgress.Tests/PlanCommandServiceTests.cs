#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.Llm.Prompting;
using Guyabano.WebTerminal.Services;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Options;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Slice 3 of model-authored workflows: the <c>/plan</c> web command
/// authors DSL through the real service stack and reports the admission
/// verdict without executing anything.
/// </summary>
public sealed class PlanCommandServiceTests
{
    private static string Digest(char c) => new string(c, 64);

    [Theory]
    [InlineData("/plan build me a todo app", true, "build me a todo app")]
    [InlineData("  /Plan echo hi", true, "echo hi")]
    [InlineData("/plan", true, "")]
    [InlineData("plan something", false, "plan something")]
    [InlineData("build me a todo app", false, "build me a todo app")]
    public void Command_detection_and_prefix_stripping(
        string prompt, bool expectedCommand, string expectedRequest)
    {
        PlanCommandService.IsPlanCommand(prompt).Should().Be(expectedCommand);
        PlanCommandService.StripPrefix(prompt).Should().Be(expectedRequest);
    }

    [Fact]
    public async Task Non_command_returns_null()
    {
        var service = CreateService(new QueueRouter([]));

        (await service.AuthorPlanAsync(
            "build me a todo app", TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task Bare_command_reports_usage()
    {
        var service = CreateService(new QueueRouter([]));

        var result = await service.AuthorPlanAsync(
            "/plan", TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Admitted.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle().Which.Should().Contain("Usage");
    }

    [Fact]
    public async Task Valid_command_returns_dsl_with_admission_verdict()
    {
        var service = CreateService(new QueueRouter([AuthoredDsl()]));

        var result = await service.AuthorPlanAsync(
            "/plan Echo hi.", TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Dsl.Should().Contain("workflow demo");
        result.Admitted.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_without_plan_reports_usage()
    {
        var service = CreateService(new QueueRouter([]));

        var result = await service.ExecutePlanAsync(TestContext.Current.CancellationToken);

        result.Output.Should().BeNull();
        result.Error.Should().Contain("/plan");
    }

    [Fact]
    public async Task Run_executes_the_last_admitted_plan_with_the_original_request()
    {
        var service = CreateService(new QueueRouter([AuthoredDsl()]));
        var authored = await service.AuthorPlanAsync(
            "/plan Echo hi.", TestContext.Current.CancellationToken);
        authored!.Admitted.Should().BeTrue();

        var result = await service.ExecutePlanAsync(TestContext.Current.CancellationToken);

        result.Error.Should().BeNull();
        result.Output.Should().Contain("Echo hi.");
    }

    [Fact]
    public async Task Run_after_rejected_plan_reports_usage()
    {
        var bad = AuthoredDsl().Replace(Digest('b'), new string('9', 64));
        var service = CreateService(new QueueRouter([bad, bad, bad, bad]));
        var authored = await service.AuthorPlanAsync(
            "/plan Echo hi.", TestContext.Current.CancellationToken);
        authored!.Admitted.Should().BeFalse();

        var result = await service.ExecutePlanAsync(TestContext.Current.CancellationToken);

        result.Output.Should().BeNull();
        result.Error.Should().Contain("/plan");
    }

    [Fact]
    public async Task Rejected_command_reports_diagnostics_without_executing()
    {
        var bad = AuthoredDsl().Replace(Digest('b'), new string('9', 64));
        var service = CreateService(new QueueRouter([bad, bad, bad, bad]));

        var result = await service.AuthorPlanAsync(
            "/plan Echo hi.", TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Admitted.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
        result.Dsl.Should().NotBeNullOrWhiteSpace();
    }

    private static PlanCommandService CreateService(QueueRouter router)
    {
        var promptsRoot = FindPromptsRoot();
        var templateEngine = new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot));
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        ContentDigest DigestOf(char c) => new("sha256", "descriptor/v1", Digest(c));
        var context = new DescriptorReference(DescriptorKind.ContextProvider, "sample.context", "1", DigestOf('a'));
        var echo = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", DigestOf('b'));
        var entries = new[]
        {
            new TrustedCatalogueDescriptor(context, callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("request", str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(echo, callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("value", str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        };
        var catalogue = new InMemoryTrustedCatalogue(entries);
        return new PlanCommandService(
            new WorkflowAuthor(
                router, new WorkflowAuthoringPromptBuilder(templateEngine), maxAttempts: 3),
            new PlanCommandCatalogue(catalogue, CatalogueSummaryBuilder.Render(entries)),
            new FuwenZhinuExecutionPorts(
                new EchoActivity(), new EchoContext(), new EchoInference()),
            Options.Create(new CodeGenerationWorkerOptions()));
    }

    private sealed class EchoActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private sealed class EchoContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken ct = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input));
            var snap = new ContextSnapshotReference(request.Provider, "snap-test",
                new ContentDigest("sha256", "request/v1", new string('c', 64)),
                new ContentDigest("sha256", "content/v1", new string('d', 64)), [],
                "policy/1", new ContextSnapshotBudgetEvidence(false, null, null, null, null), DateTimeOffset.UtcNow);
            return ValueTask.FromResult(ContextExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement), snap));
        }
    }

    private sealed class EchoInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken ct = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input));
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private static string AuthoredDsl() => $$"""
        workflow demo(input: string) -> string {
          context ctx = context "sample.context@1#{{Digest('a')}}" (request: input;) -> string;
          activity echo = activity "sample.echo@1#{{Digest('b')}}" (value: ctx;) -> string;
          return echo;
        }
        """;

    private static string FindPromptsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "prompts", "workflow-authoring", "system.sbn");
            if (File.Exists(candidate))
                return Path.Combine(directory.FullName, "prompts");
        }
        throw new DirectoryNotFoundException("Could not locate the Guyabano prompts root.");
    }

    private sealed class QueueRouter(IReadOnlyList<string> script) : ILlmRouter
    {
        private int next;
        public int RequestCalls { get; private set; }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            RequestCalls++;
            var index = Math.Min(next, script.Count - 1);
            next++;
            return StreamSingle(script.Count == 0 ? "workflow demo(input: string) -> string { return input; }" : script[index]);
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
