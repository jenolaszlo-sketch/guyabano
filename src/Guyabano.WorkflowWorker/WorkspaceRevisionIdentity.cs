using System.Security.Cryptography;
using System.Text;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowWorker;

internal static class WorkspaceRevisionIdentity
{
    public static async Task<string> ComputeAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var snapshot = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(
            workspacePath,
            cancellationToken).ConfigureAwait(false);
        var canonical = string.Join(
            "|",
            snapshot.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value.Hash}"));
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
