using System.Security.Cryptography;
using System.Text;
using Penghou.Cangjie;

namespace Guyabano.WorkflowWorker;

public static class CangjieSnapshotHelper
{
    public static async Task<ContextSnapshot> EnsureSnapshotAsync(
        IContextStore contextStore,
        string sessionId,
        string workflowRunId,
        string stepKey,
        int stepRevision,
        string queryIdentity,
        string strategy,
        string strategyVersion,
        string purpose,
        string? workspaceRevision,
        string? hetuIndexRunId,
        string? hetuIndexIdentity,
        IReadOnlyList<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        var deterministicId = CreateDeterministicId(
            sessionId,
            workflowRunId,
            stepKey,
            stepRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            purpose,
            strategy,
            strategyVersion,
            queryIdentity,
            workspaceRevision ?? string.Empty,
            hetuIndexRunId ?? string.Empty,
            hetuIndexIdentity ?? string.Empty,
            string.Join(",", itemIds.Select(id => id.ToString("D"))));
        var existing = await contextStore.GetSnapshotAsync(deterministicId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionId"] = sessionId,
            ["workflowRunId"] = workflowRunId,
            ["stepKey"] = stepKey,
            ["stepRevision"] = stepRevision.ToString(),
            ["strategy"] = strategy,
            ["strategyVersion"] = strategyVersion,
            ["queryIdentity"] = queryIdentity,
            ["purpose"] = purpose
        };
        if (workspaceRevision is not null) metadata["workspaceRevision"] = workspaceRevision;
        if (hetuIndexRunId is not null) metadata["hetuIndexRunId"] = hetuIndexRunId;
        if (hetuIndexIdentity is not null) metadata["hetuIndexIdentity"] = hetuIndexIdentity;

        var snapshot = new ContextSnapshot
        {
            Id = deterministicId,
            ItemIds = itemIds.ToArray(),
            QueryIdentity = queryIdentity,
            Strategy = strategy,
            StrategyVersion = strategyVersion,
            Purpose = purpose,
            Metadata = metadata
        };

        return await contextStore.StoreSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    private static Guid CreateDeterministicId(params string[] parts)
    {
        var combined = string.Join("\n", parts.Where(p => p is not null));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return new Guid(hash.AsSpan(0, 16));
    }
}
