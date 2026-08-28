using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Penghou.Baize;
using Penghou.Baize.Router;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.Prompting;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Decorates <see cref="ILlmRouter"/> to persist one bounded <c>baize-execution</c>
/// artifact per model invocation, correlated to the active Guyabano session, Zhinu
/// step, Cangjie snapshot, and Hetu publication. Raw prompts and responses are never
/// stored; only content hashes and bounded metadata.
/// </summary>
public sealed class BaizeExecutionProvenanceRouter(
    ILlmRouter inner,
    IArtifactRepository artifactRepository,
    ILogger<BaizeExecutionProvenanceRouter> logger) : ILlmRouter
{
    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        string model,
        LlmRequest request,
        CancellationToken cancellationToken) =>
        RecordAsync("model", model, request, cancellationToken,
            () => inner.StreamAsync(model, request, cancellationToken));

    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        Penghou.Baize.ModelStrategy strategy,
        LlmRequest request,
        CancellationToken cancellationToken) =>
        RecordAsync("strategy", strategy.ToString(), request, cancellationToken,
            () => inner.StreamAsync(strategy, request, cancellationToken));

    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        string model,
        ILlmPromptBuilder builder,
        CancellationToken cancellationToken)
    {
        var request = builder.Build(ModelStrategy.Auto);
        return RecordAsync("model", model, request, cancellationToken,
            () => inner.StreamAsync(model, request, cancellationToken));
    }

    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        Penghou.Baize.ModelStrategy strategy,
        ILlmPromptBuilder builder,
        CancellationToken cancellationToken)
    {
        var request = builder.Build(strategy);
        return RecordAsync("strategy", strategy.ToString(), request,
            cancellationToken,
            () => inner.StreamAsync(strategy, request, cancellationToken));
    }

    public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
        string route,
        ILlmPromptBuilder builder,
        CancellationToken cancellationToken)
    {
        var request = builder.Build(ModelStrategy.Auto);
        return RecordAsync("route", route, request, cancellationToken,
            () => inner.StreamRouteAsync(route, request, cancellationToken));
    }

    public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
        string route,
        LlmRequest request,
        CancellationToken cancellationToken) =>
        RecordAsync("route", route, request, cancellationToken,
            () => inner.StreamRouteAsync(route, request, cancellationToken));

#pragma warning disable CS0618
    public ResolvedEndpoint Resolve(string model) => inner.Resolve(model);

    public ResolvedEndpoint Resolve(Penghou.Baize.ModelStrategy strategy) =>
        inner.Resolve(strategy);
#pragma warning restore CS0618

    public Task<ResolvedEndpoint> ResolveAsync(
        string model,
        CancellationToken cancellationToken) =>
        inner.ResolveAsync(model, cancellationToken);

    public Task<ResolvedEndpoint> ResolveAsync(
        Penghou.Baize.ModelStrategy strategy,
        CancellationToken cancellationToken) =>
        inner.ResolveAsync(strategy, cancellationToken);

    public Task<ResolvedEndpoint> ResolveRouteAsync(
        string route,
        CancellationToken cancellationToken) =>
        inner.ResolveRouteAsync(route, cancellationToken);

    public Task<LlmRouteExplanation> ExplainModelAsync(
        string model,
        LlmRequest? request = null,
        CancellationToken cancellationToken = default) =>
        inner.ExplainModelAsync(model, request, cancellationToken);

    public Task<LlmRouteExplanation> ExplainStrategyAsync(
        Penghou.Baize.ModelStrategy strategy,
        LlmRequest? request = null,
        CancellationToken cancellationToken = default) =>
        inner.ExplainStrategyAsync(strategy, request, cancellationToken);

    public Task<LlmRouteExplanation> ExplainRouteAsync(
        string route,
        LlmRequest? request = null,
        CancellationToken cancellationToken = default) =>
        inner.ExplainRouteAsync(route, request, cancellationToken);

    private async IAsyncEnumerable<LlmStreamEvent> RecordAsync(
        string purposePrefix,
        string requestedModel,
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        Func<IAsyncEnumerable<LlmStreamEvent>> produce)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var requestHash = Hash(JsonSerializer.Serialize(
            new
            {
                messages = request.Messages.Select(message => new
                {
                    role = message.Role,
                    content = message.Parts
                }),
                tools = request.Tools.Select(tool => tool.Name),
                model = requestedModel
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var success = false;
        string? error = null;
        LlmStreamEvent? last = null;
        using var responseHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            await using var enumerator = produce()
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    throw;
                }

                if (!hasNext)
                {
                    success = true;
                    break;
                }

                var streamEvent = enumerator.Current;
                if (streamEvent is null)
                {
                    error = "The Baize router returned a null stream event.";
                    throw new InvalidOperationException(error);
                }
                last = streamEvent;
                AppendHash(responseHash, streamEvent.Delta);
                AppendHash(responseHash, streamEvent.ReasoningContent);
                yield return streamEvent;
            }
        }
        finally
        {
            try
            {
                using var persistenceTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));
                await PersistAsync(
                    purposePrefix,
                    requestedModel,
                    request,
                    requestHash,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    last,
                    Convert.ToHexString(responseHash.GetHashAndReset())
                        .ToLowerInvariant(),
                    success,
                    error,
                    persistenceTimeout.Token);
            }
            catch (Exception persistException)
            {
                logger.LogWarning(persistException,
                    "Unable to persist Baize execution provenance for model {Model}.",
                    requestedModel);
            }
        }

    }

    private async Task PersistAsync(
        string purposePrefix,
        string requestedModel,
        LlmRequest request,
        string requestHash,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        LlmStreamEvent? last,
        string responseHash,
        bool success,
        string? error,
        CancellationToken cancellationToken)
    {
        var correlation = LlmRequestCorrelationScope.Current;
        if (correlation is null)
            return;

        var purpose = purposePrefix == "model"
            ? (correlation.CangjiePurpose ?? "model-call")
            : $"{purposePrefix}:{requestedModel}";

        var diagnostics = last?.Diagnostics;
        var usage = last?.Usage;
        var routerDiagnostics = last?.RouterDiagnostics;
        var attempts = (routerDiagnostics?.Attempts ?? [])
            .Select(attempt => new BaizeRouterAttemptRecord(
                attempt.EndpointId,
                attempt.EndpointModel,
                attempt.EndpointApiStyle,
                attempt.EndpointProvider,
                attempt.Outcome.ToString(),
                attempt.Duration,
                attempt.Error,
                attempt.UnavailableUntil?.ToString("O")))
            .ToArray();
        var rateLimit = last?.RateLimit is null
            ? null
            : new BaizeRateLimitRecord(
                last.RateLimit.RequestsRemaining,
                last.RateLimit.RequestsLimit,
                last.RateLimit.RequestsResetAt,
                last.RateLimit.TokensRemaining,
                last.RateLimit.TokensLimit,
                last.RateLimit.TokensResetAt,
                last.RateLimit.RetryAfter,
                last.RateLimit.UnavailableUntil);

        var record = new BaizeExecutionRecord(
            SessionId: correlation.SessionId,
            WorkflowRunId: correlation.WorkflowRunId,
            WorkflowStepKey: correlation.WorkflowStepKey,
            WorkflowStepRevision: correlation.WorkflowStepRevision,
            CangjieSnapshotId: correlation.CangjieSnapshotId,
            CangjieStrategy: correlation.CangjieStrategy,
            CangjieStrategyVersion: correlation.CangjieStrategyVersion,
            CangjieQueryIdentity: correlation.CangjieQueryIdentity,
            HetuIndexRunId: correlation.HetuIndexRunId,
            HetuIndexIdentity: correlation.HetuIndexIdentity,
            WorkspaceRevision: correlation.WorkspaceRevision,
            Purpose: purpose,
            RequestedModel: requestedModel,
            Provider: diagnostics?.Provider,
            ActualModel: diagnostics?.ActualModel,
            ApiStyle: diagnostics?.Api,
            RouterAttempts: attempts,
            PromptTokens: usage?.PromptTokens,
            CompletionTokens: usage?.CompletionTokens,
            TotalTokens: usage?.TotalTokens,
            PromptCacheHitTokens: usage?.PromptCacheHitTokens,
            PromptCacheMissTokens: usage?.PromptCacheMissTokens,
            TotalDurationMilliseconds: diagnostics?.TotalDurationMilliseconds,
            LoadDurationMilliseconds: diagnostics?.LoadDurationMilliseconds,
            PromptEvaluationDurationMilliseconds: diagnostics?.PromptEvaluationDurationMilliseconds,
            GenerationDurationMilliseconds: diagnostics?.GenerationDurationMilliseconds,
            GenerationTokensPerSecond: diagnostics?.GenerationTokensPerSecond,
            NativeToolCallCount: diagnostics?.NativeToolCallCount,
            FinishReason: last?.FinishReason,
            FinishReasonKind: last is null ? null : last.FinishReasonKind.ToString(),
            ContentWasRepaired: last?.ContentWasRepaired ?? false,
            ContentRepairAttemptCount: last?.ContentRepairAttempts?.Count ?? 0,
            RateLimit: rateLimit,
            ResponseId: diagnostics?.ResponseId,
            RequestHash: requestHash,
            ResponseHash: last is null && !success ? null : responseHash,
            Succeeded: success,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            Error: error);

        var workflowId = correlation.WorkflowRunId;
        var stageKey = $"{purpose}/{correlation.WorkflowStepKey}";
        await artifactRepository.WriteAsync(
            new ArtifactWriteRequest<BaizeExecutionRecord>(
                WorkflowId: workflowId,
                Kind: "baize-execution",
                SchemaVersion: 1,
                StageKey: stageKey,
                Status: success ? ArtifactStatus.Validated : ArtifactStatus.Produced,
                Payload: record),
            cancellationToken).ConfigureAwait(false);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void AppendHash(
        IncrementalHash hash,
        string? value)
    {
        if (value is null)
            return;

        hash.AppendData(Encoding.UTF8.GetBytes(value));
    }
}
