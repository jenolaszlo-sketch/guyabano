using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.Prompting;
using Guyabano.Session;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Penghou.Baize;
using Penghou.Baize.Router;

namespace Guyabano.WorkflowProgressTests;

public sealed class BaizeExecutionProvenanceTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-baize-provenance-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RouterPersistsCorrelatedExecutionRecordPerInvocation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sessionStore = new FileSystemGuyabanoSessionStore(Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: ct);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);

        var artifacts = new ZhinuPublishingArtifactRepository(
            new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts")),
            resolver);
        var inner = new StubLlmRouter();
        var router = new BaizeExecutionProvenanceRouter(
            inner,
            artifacts,
            NullLogger<BaizeExecutionProvenanceRouter>.Instance);

        var snapshotId = Guid.NewGuid();
        using (LlmRequestCorrelationScope.Push(new(
            session.Id.ToString(),
            runId.ToString("D"),
            "planning",
            CangjieSnapshotId: snapshotId,
            CangjieStrategy: "hetu-public-surface-and-symbol-neighborhood",
            CangjieStrategyVersion: "1",
            CangjieQueryIdentity: $"guyabano:{runId:D}:repository-context",
            CangjiePurpose: "code-generation-planning",
            HetuIndexRunId: "hetu-run-1",
            HetuIndexIdentity: "ws-rev-abc",
            WorkspaceRevision: "ws-rev-abc",
            WorkflowStepRevision: 2)))
        {
            var request = new LlmRequest(
                [new LlmMessage("system", "sys"), new LlmMessage("user", "hello")],
                temperature: 0.2,
                maxTokens: 2048,
                tools: [],
                responseFormat: null,
                metadata: new Dictionary<string, object?>());
            var response = await router.CompleteStreamingAsync(
                "test-model",
                request,
                ct);
            response.Content.Should().Be("hello world");
        }

        var envelope = await artifacts.ReadLatestAsync<BaizeExecutionRecord>(
            runId.ToString("D"),
            "baize-execution",
            "code-generation-planning/planning",
            ct);
        envelope.Should().NotBeNull();
        var record = envelope!.Payload;
        record.SessionId.Should().Be(session.Id.ToString());
        record.WorkflowRunId.Should().Be(runId.ToString("D"));
        record.WorkflowStepKey.Should().Be("planning");
        record.WorkflowStepRevision.Should().Be(2);
        record.CangjieSnapshotId.Should().Be(snapshotId);
        record.CangjieStrategy.Should().Be("hetu-public-surface-and-symbol-neighborhood");
        record.HetuIndexRunId.Should().Be("hetu-run-1");
        record.Purpose.Should().Be("code-generation-planning");
        record.RequestedModel.Should().Be("test-model");
        record.Provider.Should().Be("stub-provider");
        record.ActualModel.Should().Be("stub-model");
        record.PromptTokens.Should().Be(10);
        record.CompletionTokens.Should().Be(5);
        record.TotalTokens.Should().Be(15);
        record.FinishReason.Should().Be("stop");
        record.FinishReasonKind.Should().Be("Stop");
        record.Succeeded.Should().BeTrue();
        record.RequestHash.Should().NotBeNullOrWhiteSpace();
        record.ResponseHash.Should().Be(Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("hello world")))
            .ToLowerInvariant());
        record.RouterAttempts.Should().ContainSingle(a => a.EndpointId == "endpoint-1" && a.Outcome == "Succeeded");
        record.RateLimit.Should().NotBeNull();
        record.RateLimit!.RequestsRemaining.Should().Be(99);
        envelope.SessionId.Should().Be(session.Id.ToString());
    }

    [Fact]
    public async Task FailedInvocationPersistsRecordWithErrorAndNoRawPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sessionStore = new FileSystemGuyabanoSessionStore(Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: ct);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var artifacts = new ZhinuPublishingArtifactRepository(
            new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts")),
            resolver);
        var router = new BaizeExecutionProvenanceRouter(
            new ThrowingLlmRouter(),
            artifacts,
            NullLogger<BaizeExecutionProvenanceRouter>.Instance);

        using (LlmRequestCorrelationScope.Push(new(
            session.Id.ToString(),
            runId.ToString("D"),
            "planning",
            CangjiePurpose: "code-generation-planning")))
        {
            var request = new LlmRequest(
                [new LlmMessage("user", "secret-sensitive-prompt")],
                temperature: null,
                maxTokens: null,
                tools: [],
                responseFormat: null,
                metadata: new Dictionary<string, object?>());
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                router.CompleteStreamingAsync("test-model", request, ct));
        }

        var envelope = await artifacts.ReadLatestAsync<BaizeExecutionRecord>(
            runId.ToString("D"),
            "baize-execution",
            "code-generation-planning/planning",
            ct);
        envelope.Should().NotBeNull();
        envelope!.Payload.Succeeded.Should().BeFalse();
        envelope.Payload.Error.Should().NotBeNullOrWhiteSpace();
        envelope.Payload.FinishReasonKind.Should().BeNull();
        // Raw prompt must never be persisted
        var rawPath = System.IO.Path.Combine(
            rootPath,
            ".gen",
            "artifacts",
            envelope.Reference.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var raw = await System.IO.File.ReadAllTextAsync(rawPath, ct);
        raw.Should().NotContain("secret-sensitive-prompt");
    }

    [Fact]
    public async Task RouterYieldsFirstTokenBeforeProviderCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new StubLlmRouter(gated: true);
        var router = new BaizeExecutionProvenanceRouter(
            inner,
            new FileSystemArtifactRepository(
                Path.Combine(rootPath, ".gen", "stream-artifacts")),
            NullLogger<BaizeExecutionProvenanceRouter>.Instance);
        var request = new LlmRequest(
            [new LlmMessage("user", "hello")],
            temperature: null,
            maxTokens: null,
            tools: [],
            responseFormat: null,
            metadata: new Dictionary<string, object?>());

        await using var enumerator = router.StreamAsync(
                "test-model",
                request,
                ct)
            .GetAsyncEnumerator(ct);

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Delta.Should().Be("hello ");
        inner.ProviderCompleted.Should().BeFalse();

        inner.Release();
        while (await enumerator.MoveNextAsync())
        {
        }
        inner.ProviderCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task BuilderAndRouteOverloadsPassThroughRecordedRequestPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new StubLlmRouter();
        var router = new BaizeExecutionProvenanceRouter(
            inner,
            new FileSystemArtifactRepository(
                Path.Combine(rootPath, ".gen", "overload-artifacts")),
            NullLogger<BaizeExecutionProvenanceRouter>.Instance);
        var builder = new StubPromptBuilder();

        await router.StreamAsync("model", builder, ct).ToListAsync(ct);
        await router.StreamAsync(ModelStrategy.Auto, builder, ct).ToListAsync(ct);
        await router.StreamRouteAsync("planning", builder, ct).ToListAsync(ct);
        await router.StreamRouteAsync(
            "planning",
            builder.Build(ModelStrategy.Auto),
            ct).ToListAsync(ct);

        inner.RequestCalls.Should().Be(2);
        inner.BuilderCalls.Should().Be(0,
            "the decorator must materialize requests so provenance sees them");
        inner.RouteRequestCalls.Should().Be(2);
        inner.RouteBuilderCalls.Should().Be(0);
    }

    [Fact]
    public async Task CancellationStillPersistsPartialExecutionWithIndependentToken()
    {
        using var cts = new CancellationTokenSource();
        var runId = Guid.NewGuid();
        var artifacts = new FileSystemArtifactRepository(
            Path.Combine(rootPath, ".gen", "cancel-artifacts"));
        var router = new BaizeExecutionProvenanceRouter(
            new StubLlmRouter(gated: true),
            artifacts,
            NullLogger<BaizeExecutionProvenanceRouter>.Instance);
        var request = new StubPromptBuilder().Build(ModelStrategy.Auto);

        using (LlmRequestCorrelationScope.Push(new(
            GuyabanoSessionId.New().ToString(),
            runId.ToString("D"),
            "planning",
            CangjiePurpose: "code-generation-planning")))
        {
            await using var enumerator = router.StreamAsync(
                    "test-model",
                    request,
                    cts.Token)
                .GetAsyncEnumerator(cts.Token);
            (await enumerator.MoveNextAsync()).Should().BeTrue();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await enumerator.MoveNextAsync().AsTask());
        }

        var envelope = await artifacts.ReadLatestAsync<BaizeExecutionRecord>(
            runId.ToString("D"),
            "baize-execution",
            "code-generation-planning/planning",
            CancellationToken.None);
        envelope.Should().NotBeNull();
        envelope!.Payload.Succeeded.Should().BeFalse();
        envelope.Payload.ResponseHash.Should().NotBeNullOrWhiteSpace();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private sealed class StubLlmRouter : ILlmRouter
    {
        private readonly TaskCompletionSource? release;

        public StubLlmRouter(bool gated = false)
        {
            if (gated)
                release = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool ProviderCompleted { get; private set; }
        public int RequestCalls { get; private set; }
        public int BuilderCalls { get; private set; }
        public int RouteRequestCalls { get; private set; }
        public int RouteBuilderCalls { get; private set; }

        public void Release() => release?.TrySetResult();

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model,
            LlmRequest request,
            CancellationToken cancellationToken)
        {
            RequestCalls++;
            return StreamAsyncCore(model, request, cancellationToken);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            Penghou.Baize.ModelStrategy strategy,
            LlmRequest request,
            CancellationToken cancellationToken)
        {
            RequestCalls++;
            return StreamAsyncCore(strategy.ToString(), request, cancellationToken);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model,
            ILlmPromptBuilder builder,
            CancellationToken cancellationToken)
        {
            BuilderCalls++;
            return StreamAsyncCore(
                model,
                builder.Build(Penghou.Baize.ModelStrategy.Auto),
                cancellationToken);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            Penghou.Baize.ModelStrategy strategy,
            ILlmPromptBuilder builder,
            CancellationToken cancellationToken)
        {
            BuilderCalls++;
            return StreamAsyncCore(
                strategy.ToString(),
                builder.Build(strategy),
                cancellationToken);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
            string route,
            ILlmPromptBuilder builder,
            CancellationToken cancellationToken)
        {
            RouteBuilderCalls++;
            return StreamAsyncCore(
                route,
                builder.Build(Penghou.Baize.ModelStrategy.Auto),
                cancellationToken);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
            string route,
            LlmRequest request,
            CancellationToken cancellationToken)
        {
            RouteRequestCalls++;
            return StreamAsyncCore(route, request, cancellationToken);
        }

        private async IAsyncEnumerable<LlmStreamEvent> StreamAsyncCore(
            string model,
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new LlmStreamEvent(
                Delta: "hello ",
                ReasoningContent: null,
                FinishReason: null,
                Usage: null,
                ToolCallDelta: null,
                Diagnostics: null,
                RateLimit: null,
                RouterDiagnostics: null,
                Continuation: null);
            if (release is not null)
                await release.Task.WaitAsync(cancellationToken);
            yield return new LlmStreamEvent(
                Delta: "world",
                ReasoningContent: null,
                FinishReason: "stop",
                Usage: new LlmUsage(10, 5, 15, 4, 6, null),
                ToolCallDelta: null,
                Diagnostics: new LlmProviderDiagnostics(
                    "stub-provider", "stub-model", "stub-api", true, "stop", 120.5, 10, 20, 90, 45.5, 2, 11, "resp-1", null, null, null),
                RateLimit: new LlmRateLimitInfo { RequestsRemaining = 99, RequestsLimit = 100 },
                RouterDiagnostics: new LlmRouterDiagnostics(
                    [new LlmRouterAttempt("endpoint-1", "stub-model", "stub-api", LlmRouterAttemptOutcome.Succeeded, TimeSpan.FromMilliseconds(110), null, null)]),
                Continuation: null);
            ProviderCompleted = true;
        }

        public ResolvedEndpoint Resolve(string model) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(string model, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ResolvedEndpoint Resolve(Penghou.Baize.ModelStrategy strategy) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(Penghou.Baize.ModelStrategy strategy, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveRouteAsync(string route, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainModelAsync(string model, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainStrategyAsync(Penghou.Baize.ModelStrategy strategy, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainRouteAsync(string route, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class StubPromptBuilder : ILlmPromptBuilder
    {
        public LlmRequest Build(ModelStrategy strategy) => new(
            [new LlmMessage("user", "hello")],
            temperature: null,
            maxTokens: null,
            tools: [],
            responseFormat: null,
            metadata: new Dictionary<string, object?>());
    }

    private sealed class ThrowingLlmRouter : ILlmRouter
    {
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(string model, LlmRequest request, CancellationToken cancellationToken) =>
            Throw();
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(Penghou.Baize.ModelStrategy strategy, LlmRequest request, CancellationToken cancellationToken) => Throw();
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(string model, ILlmPromptBuilder builder, CancellationToken cancellationToken) => Throw();
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(Penghou.Baize.ModelStrategy strategy, ILlmPromptBuilder builder, CancellationToken cancellationToken) => Throw();
        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(string route, ILlmPromptBuilder builder, CancellationToken cancellationToken) => Throw();
        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(string route, LlmRequest request, CancellationToken cancellationToken) => Throw();

        private static async IAsyncEnumerable<LlmStreamEvent> Throw()
        {
            await Task.Yield();
            yield return null!;
            throw new InvalidOperationException("provider down");
        }

        public ResolvedEndpoint Resolve(string model) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(string model, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ResolvedEndpoint Resolve(Penghou.Baize.ModelStrategy strategy) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(Penghou.Baize.ModelStrategy strategy, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveRouteAsync(string route, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainModelAsync(string model, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainStrategyAsync(Penghou.Baize.ModelStrategy strategy, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainRouteAsync(string route, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
