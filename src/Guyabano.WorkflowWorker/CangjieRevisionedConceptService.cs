using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Guyabano.CodeGeneration.Planning;
using Penghou.Cangjie;

namespace Guyabano.WorkflowWorker;

public sealed class CangjieRevisionedConceptService(IContextStore contextStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ContextItem> StoreDecisionAsync(
        string sessionId,
        ArchitectureDecision decision,
        string workflowRunId,
        string stepKey,
        int stepRevision,
        string? repositoryId = null,
        IReadOnlyList<Guid>? derivedFromIds = null,
        IReadOnlyList<Guid>? supportsIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.Id);

        var scope = SessionScope(sessionId, repositoryId);
        var key = DecisionKey(decision.Id);
        var content = JsonSerializer.Serialize(decision, JsonOptions);
        var hash = Hash(content);
        var tags = new List<string> { "decision", $"decision:{decision.Id}", $"session:{sessionId}" };
        if (repositoryId is not null) tags.Add($"repository:{repositoryId}");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["decisionId"] = decision.Id,
            ["sessionId"] = sessionId,
            ["workflowRunId"] = workflowRunId,
            ["stepKey"] = stepKey,
            ["stepRevision"] = stepRevision.ToString(),
            ["contentHash"] = hash
        };
        if (repositoryId is not null) metadata["repositoryId"] = repositoryId;

        var provenance = new ContextProvenance
        {
            Source = new ContextSource { Uri = $"guyabano://session/{sessionId}/decision/{decision.Id}", Kind = "guyabano-decision", ContentHash = hash },
            Producer = "guyabano:architecture-decision",
            ProducerVersion = "1",
            Attributes = new Dictionary<string, string>
            {
                ["sessionId"] = sessionId,
                ["workflowRunId"] = workflowRunId,
                ["stepKey"] = stepKey,
                ["stepRevision"] = stepRevision.ToString()
            }
        };

        return await StoreRevisionedAsync(
            scope, key, ContextKinds.Decision, content, provenance, metadata, tags,
            derivedFromIds, supportsIds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextItem> StoreEvidenceAsync(
        string sessionId,
        string evidenceKey,
        string content,
        string workflowRunId,
        string stepKey,
        int stepRevision,
        string? repositoryId = null,
        IReadOnlyList<Guid>? supportsIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var scope = SessionScope(sessionId, repositoryId);
        var key = EvidenceKey(evidenceKey);
        var hash = Hash(content);
        var tags = new List<string> { "evidence", $"evidence:{evidenceKey}", $"session:{sessionId}" };
        if (repositoryId is not null) tags.Add($"repository:{repositoryId}");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["evidenceKey"] = evidenceKey,
            ["sessionId"] = sessionId,
            ["workflowRunId"] = workflowRunId,
            ["stepKey"] = stepKey,
            ["stepRevision"] = stepRevision.ToString(),
            ["contentHash"] = hash
        };
        if (repositoryId is not null) metadata["repositoryId"] = repositoryId;

        var provenance = new ContextProvenance
        {
            Source = new ContextSource { Uri = $"guyabano://session/{sessionId}/evidence/{evidenceKey}", Kind = "guyabano-evidence", ContentHash = hash },
            Producer = "guyabano:evidence",
            ProducerVersion = "1",
            Attributes = new Dictionary<string, string>
            {
                ["sessionId"] = sessionId,
                ["workflowRunId"] = workflowRunId,
                ["stepKey"] = stepKey
            }
        };

        return await StoreRevisionedAsync(scope, key, ContextKinds.Evidence, content, provenance, metadata, tags, null, supportsIds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextItem> StoreKnowledgeAsync(
        string sessionId,
        string knowledgeKey,
        string content,
        string workflowRunId,
        string stepKey,
        int stepRevision,
        string? repositoryId = null,
        IReadOnlyList<Guid>? derivedFromIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var scope = SessionScope(sessionId, repositoryId);
        var key = KnowledgeKey(knowledgeKey);
        var hash = Hash(content);
        var tags = new List<string> { "knowledge", $"knowledge:{knowledgeKey}", $"session:{sessionId}" };
        if (repositoryId is not null) tags.Add($"repository:{repositoryId}");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["knowledgeKey"] = knowledgeKey,
            ["sessionId"] = sessionId,
            ["workflowRunId"] = workflowRunId,
            ["stepKey"] = stepKey,
            ["stepRevision"] = stepRevision.ToString(),
            ["contentHash"] = hash
        };

        var provenance = new ContextProvenance
        {
            Source = new ContextSource { Uri = $"guyabano://session/{sessionId}/knowledge/{knowledgeKey}", Kind = "guyabano-knowledge", ContentHash = hash },
            Producer = "guyabano:knowledge",
            ProducerVersion = "1",
            Attributes = new Dictionary<string, string>
            {
                ["sessionId"] = sessionId,
                ["workflowRunId"] = workflowRunId,
                ["stepKey"] = stepKey
            }
        };

        return await StoreRevisionedAsync(scope, key, ContextKinds.Knowledge, content, provenance, metadata, tags, derivedFromIds, null, cancellationToken).ConfigureAwait(false);
    }

    public Task<ContextItem?> GetLatestDecisionAsync(string sessionId, string decisionId, string? repositoryId = null, CancellationToken cancellationToken = default)
    {
        var scope = SessionScope(sessionId, repositoryId);
        var key = DecisionKey(decisionId);
        return contextStore.GetLatestByKeyAsync(scope, key, cancellationToken).AsTask();
    }

    public Task<IReadOnlyList<ContextItem>> GetDecisionHistoryAsync(string sessionId, string decisionId, string? repositoryId = null, CancellationToken cancellationToken = default)
    {
        var scope = SessionScope(sessionId, repositoryId);
        var key = DecisionKey(decisionId);
        return contextStore.GetHistoryByKeyAsync(scope, key, cancellationToken).AsTask();
    }

    private async Task<ContextItem> StoreRevisionedAsync(
        string scope,
        string key,
        string kind,
        string content,
        ContextProvenance provenance,
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyCollection<string> tags,
        IReadOnlyList<Guid>? derivedFromIds,
        IReadOnlyList<Guid>? supportsIds,
        CancellationToken cancellationToken)
    {
        var hash = Hash(content);
        var idempotencyKey = $"{scope}:{key}:{hash}";
        var previous = await contextStore.GetLatestByKeyAsync(scope, key, cancellationToken).ConfigureAwait(false);

        // If previous exists and has same hash, return it (idempotent)
        if (previous is not null && previous.Metadata.TryGetValue("contentHash", out var prevHash) && prevHash == hash)
            return previous;

        var item = new ContextItem
        {
            Scope = scope,
            Key = key,
            Kind = kind,
            Content = content,
            Provenance = provenance,
            Metadata = metadata,
            Tags = tags
        };

        var stored = await contextStore.StoreAsync(item, new ContextWriteOptions { IdempotencyKey = idempotencyKey }, cancellationToken).ConfigureAwait(false);

        if (previous is not null && previous.Id != stored.Id)
        {
            await contextStore.AddRelationAsync(new ContextRelation
            {
                FromId = stored.Id,
                ToId = previous.Id,
                Kind = ContextRelationKinds.Supersedes
            }, cancellationToken).ConfigureAwait(false);
        }

        if (derivedFromIds is not null)
        {
            foreach (var fromId in derivedFromIds)
            {
                await contextStore.AddRelationAsync(new ContextRelation
                {
                    FromId = stored.Id,
                    ToId = fromId,
                    Kind = ContextRelationKinds.DerivedFrom
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        if (supportsIds is not null)
        {
            foreach (var toId in supportsIds)
            {
                await contextStore.AddRelationAsync(new ContextRelation
                {
                    FromId = stored.Id,
                    ToId = toId,
                    Kind = ContextRelationKinds.Supports
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        return stored;
    }

    private static string SessionScope(string sessionId, string? repositoryId) =>
        repositoryId is null ? $"guyabano:session:{sessionId}" : $"guyabano:session:{sessionId}:repository:{repositoryId}";

    private static string DecisionKey(string id) => $"decision:{id}";
    private static string EvidenceKey(string key) => $"evidence:{key}";
    private static string KnowledgeKey(string key) => $"knowledge:{key}";

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
