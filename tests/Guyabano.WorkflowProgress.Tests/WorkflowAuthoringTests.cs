#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Guihua;
using Penghou.Guihua.Baize;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Slice 1 of model-authored workflows: the meta-prompt pack teaches the
/// DSL, a scripted model emits workflow source, and the real compiler
/// admits it. UI display and execution come later.
/// </summary>
public sealed class WorkflowAuthoringTests
{
    private static string Digest(char c) => new string(c, 64);

    private static string CatalogueSummary() => $"""
        context-provider sample.context@1#{Digest('a')} (request: string) -> string — assembles repository context.
        activity sample.echo@1#{Digest('b')} (value: string) -> string — echoes its input.
        """;

    private static string AuthoredDsl() => $$"""
        workflow demo(input: string) -> string {
          context ctx = context "sample.context@1#{{Digest('a')}}" (request: input;) -> string;
          activity echo = activity "sample.echo@1#{{Digest('b')}}" (value: ctx;) -> string;
          return echo;
        }
        """;

    [Fact]
    public async Task Authored_dsl_compiles_and_admits_against_the_catalogue()
    {
        var ct = TestContext.Current.CancellationToken;
        var templateEngine = new ScribanPromptTemplateEngine(new EmbeddedPromptLoader());
        var authorBuilder = new WorkflowAuthoringPromptBuilder(templateEngine);
        var router = new CannedAuthorRouter(AuthoredDsl());

        // Author path: real meta-prompt pack renders the request plus the
        // catalogue the model must reference.
        var authorRequest = await authorBuilder.BuildAsync(
            new WorkflowAuthoringPromptContext("Echo my request.", CatalogueSummary(), 4000),
            ct);
        var systemText = TextOf(authorRequest, "system");
        var userText = TextOf(authorRequest, "user");
        systemText.Should().Contain("Closed regions");
        systemText.Should().Contain("sample.echo@1");
        userText.Should().Contain("Echo my request.");

        // The model's reply is untrusted text until the compiler admits it.
        var response = await router.CompleteStreamingAsync("stub-author", authorRequest, ct);
        var catalogue = TestCatalogue();
        var compiled = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(response.Content, cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(
            string.Join("; ", compiled.Diagnostics.Select(d => $"{d.Code}:{d.Message} path={d.Path}")));
        compiled.Plan!.Nodes.Should().ContainSingle(n => n is ContextNode);
        compiled.Plan.Nodes.Should().ContainSingle(n => n is ActivityNode);

        var admission = await new WorkflowAdmissionService(
                new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(compiled.Plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message}")));
        admission.Receipt.Should().NotBeNull();
    }

    private static ITrustedCatalogue TestCatalogue()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        ContentDigest DigestOf(char c) => new("sha256", "descriptor/v1", Digest(c));
        return new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.ContextProvider, "sample.context", "1", DigestOf('a')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("request", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", DigestOf('b')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("value", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        ]);
    }

    private static string TextOf(LlmRequest request, string role) => string.Concat(request.Messages
        .Where(m => m.Role == role)
        .SelectMany(m => m.Parts)
        .OfType<LlmTextContent>()
        .Select(p => p.Text));

    [Fact]
    public async Task Repair_loop_recovers_after_compiler_rejection()
    {
        var ct = TestContext.Current.CancellationToken;
        var templateEngine = new ScribanPromptTemplateEngine(new EmbeddedPromptLoader());
        var router = new CannedAuthorRouter(
        [
            AuthoredDsl().Replace(Digest('b'), new string('9', 64)),
            AuthoredDsl(),
        ]);
        var author = new WorkflowAuthor(
            router, new WorkflowAuthoringPromptBuilder(templateEngine), TestCatalogue(), maxAttempts: 3);

        var result = await author.AuthorAsync(
            "Echo my request.", CatalogueSummary(), "stub-author", 4000, ct);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Attempts.Should().HaveCount(2);
        result.Attempts[0].Admitted.Should().BeFalse();
        result.Attempts[1].Admitted.Should().BeTrue();
        result.Admission.Should().NotBeNull();
        result.Admission!.Receipt.Should().NotBeNull();
        // The compiler rejection was fed back into the retry prompt.
        var retryText = TextOf(router.Requests[1], "user");
        retryText.Should().Contain("FWN-");
        router.RequestCalls.Should().Be(2);
    }

    [Fact]
    public async Task Repair_loop_gives_up_after_max_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var templateEngine = new ScribanPromptTemplateEngine(new EmbeddedPromptLoader());
        var bad = AuthoredDsl().Replace(Digest('b'), new string('9', 64));
        var router = new CannedAuthorRouter([bad, bad, bad, bad]);
        var author = new WorkflowAuthor(
            router, new WorkflowAuthoringPromptBuilder(templateEngine), TestCatalogue(), maxAttempts: 3);

        var result = await author.AuthorAsync(
            "Echo my request.", CatalogueSummary(), "stub-author", 4000, ct);

        result.Succeeded.Should().BeFalse();
        result.Attempts.Should().HaveCount(3);
        result.Admission.Should().BeNull();
        result.Diagnostics.Should().NotBeEmpty();
        router.RequestCalls.Should().Be(3);
    }

    [Theory]
    [InlineData("```fuwen\nworkflow demo(input: string) -> string {\n  return input;\n}\n```", "workflow demo")]
    [InlineData("workflow demo(input: string) -> string {\n  return input;\n}", "workflow demo")]
    public void ExtractDsl_unwraps_optional_fences(string content, string expectedStart)
    {
        WorkflowAuthor.ExtractDsl(content).Should().StartWith(expectedStart);
        WorkflowAuthor.ExtractDsl(content).Should().NotContain("```");
    }

    private sealed class CannedAuthorRouter(IReadOnlyList<string> script) : ILlmRouter
    {
        private int next;
        public List<LlmRequest> Requests { get; } = [];
        public int RequestCalls { get; private set; }

        public CannedAuthorRouter(string dsl)
            : this([dsl])
        {
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            RequestCalls++;
            Requests.Add(request);
            var index = Math.Min(next, script.Count - 1);
            next++;
            return StreamSingle(script[index]);
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
