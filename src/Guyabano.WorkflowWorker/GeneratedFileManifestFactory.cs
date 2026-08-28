using System.Security.Cryptography;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowWorker;

public static class GeneratedFileManifestFactory
{
    public static async Task<IReadOnlyDictionary<string, (string Hash, long Length)>> SnapshotWorkspaceAsync(
        string workspaceHostPath,
        CancellationToken cancellationToken = default)
    {
        var fullRoot = Path.GetFullPath(workspaceHostPath);
        var result = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
        if (!Directory.Exists(fullRoot))
            return result;

        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            if (relative.StartsWith(".gen/", StringComparison.Ordinal) || relative.StartsWith(".gen\\", StringComparison.Ordinal))
                continue;
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            result[relative] = (hash, bytes.Length);
        }

        return result;
    }

    public static async Task<GeneratedFileManifest> CreateWithWorkspaceDiffAsync(
        string sessionId,
        string workflowRunId,
        string stepKey,
        int stepRevision,
        string workspaceHostPath,
        string workspaceCiPath,
        string taskId,
        IReadOnlyDictionary<string, (string Hash, long Length)> beforeSnapshot,
        IReadOnlyDictionary<string, (string Hash, long Length)> afterSnapshot,
        GeneratedFileManifest? previousManifest,
        IReadOnlyList<string> skippedFiles,
        IReadOnlySet<string>? currentOwnedPaths = null,
        string? parentTaskId = null,
        bool isBuildRepair = false,
        int buildRepairCycle = 0,
        string? model = null,
        int? modelTier = null,
        CodeGenerationUsage? usage = null,
        CodeGenerationDiagnostics? diagnostics = null,
        string? finishReason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceHostPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        if (currentOwnedPaths is not null)
        {
            var relevantPaths = new HashSet<string>(
                currentOwnedPaths,
                StringComparer.Ordinal);
            if (previousManifest is not null)
            {
                relevantPaths.UnionWith(previousManifest.Files.Select(file =>
                    file.RelativePath));
                relevantPaths.UnionWith((previousManifest.StaleFiles ?? [])
                    .Select(file => file.RelativePath));
            }

            beforeSnapshot = beforeSnapshot
                .Where(pair => relevantPaths.Contains(pair.Key))
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
            afterSnapshot = afterSnapshot
                .Where(pair => relevantPaths.Contains(pair.Key))
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
        }

        var entries = new List<GeneratedFileManifestEntry>();
        var before = new HashSet<string>(beforeSnapshot.Keys, StringComparer.Ordinal);
        var after = new HashSet<string>(afterSnapshot.Keys, StringComparer.Ordinal);

        var created = after.Except(before, StringComparer.Ordinal).ToList();
        var deleted = before.Except(after, StringComparer.Ordinal).ToList();
        var common = before.Intersect(after, StringComparer.Ordinal).ToList();

        // Detect renames: pair deleted and created with same hash
        var createdByHash = created
            .GroupBy(p => afterSnapshot[p].Hash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<string>(g.OrderBy(x => x, StringComparer.Ordinal)), StringComparer.Ordinal);
        var deletedByHash = deleted
            .GroupBy(p => beforeSnapshot[p].Hash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<string>(g.OrderBy(x => x, StringComparer.Ordinal)), StringComparer.Ordinal);

        var renamedPairs = new List<(string OldPath, string NewPath)>();
        var usedCreated = new HashSet<string>(StringComparer.Ordinal);
        var usedDeleted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var hash in createdByHash.Keys.Intersect(deletedByHash.Keys, StringComparer.Ordinal))
        {
            var cQueue = createdByHash[hash];
            var dQueue = deletedByHash[hash];
            while (cQueue.Count > 0 && dQueue.Count > 0)
            {
                var newPath = cQueue.Dequeue();
                var oldPath = dQueue.Dequeue();
                renamedPairs.Add((oldPath, newPath));
                usedCreated.Add(newPath);
                usedDeleted.Add(oldPath);
            }
        }

        // Renamed entries
        foreach (var (oldPath, newPath) in renamedPairs.OrderBy(p => p.NewPath, StringComparer.Ordinal))
        {
            var beforeInfo = beforeSnapshot[oldPath];
            var afterInfo = afterSnapshot[newPath];
            entries.Add(new GeneratedFileManifestEntry(
                RelativePath: newPath,
                ContentHash: afterInfo.Hash,
                ByteLength: afterInfo.Length,
                Operation: "Renamed",
                PreviousRelativePath: oldPath,
                BeforeHash: beforeInfo.Hash,
                AfterHash: afterInfo.Hash,
                BeforeByteLength: beforeInfo.Length,
                AfterByteLength: afterInfo.Length));
        }

        // Created (excluding renamed)
        foreach (var path in created.Where(p => !usedCreated.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
        {
            var afterInfo = afterSnapshot[path];
            entries.Add(new GeneratedFileManifestEntry(
                RelativePath: path,
                ContentHash: afterInfo.Hash,
                ByteLength: afterInfo.Length,
                Operation: "Created",
                BeforeHash: null,
                AfterHash: afterInfo.Hash,
                BeforeByteLength: null,
                AfterByteLength: afterInfo.Length));
        }

        // Modified
        foreach (var path in common.OrderBy(p => p, StringComparer.Ordinal))
        {
            var beforeInfo = beforeSnapshot[path];
            var afterInfo = afterSnapshot[path];
            if (!beforeInfo.Hash.Equals(afterInfo.Hash, StringComparison.Ordinal))
            {
                entries.Add(new GeneratedFileManifestEntry(
                    RelativePath: path,
                    ContentHash: afterInfo.Hash,
                    ByteLength: afterInfo.Length,
                    Operation: "Modified",
                    BeforeHash: beforeInfo.Hash,
                    AfterHash: afterInfo.Hash,
                    BeforeByteLength: beforeInfo.Length,
                    AfterByteLength: afterInfo.Length));
            }
        }

        // Deleted (excluding renamed)
        foreach (var path in deleted.Where(p => !usedDeleted.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
        {
            var beforeInfo = beforeSnapshot[path];
            entries.Add(new GeneratedFileManifestEntry(
                RelativePath: path,
                ContentHash: beforeInfo.Hash,
                ByteLength: beforeInfo.Length,
                Operation: "Deleted",
                BeforeHash: beforeInfo.Hash,
                AfterHash: null,
                BeforeByteLength: beforeInfo.Length,
                AfterByteLength: null));
        }

        // Stale detection: files previously owned by this task but not in current afterSnapshot
        var staleEntries = new List<GeneratedFileManifestEntry>();
        if (previousManifest is not null)
        {
            var currentPaths = currentOwnedPaths is null
                ? new HashSet<string>(afterSnapshot.Keys, StringComparer.Ordinal)
                : new HashSet<string>(currentOwnedPaths, StringComparer.Ordinal);
            foreach (var prevFile in previousManifest.Files)
            {
                if (!currentPaths.Contains(prevFile.RelativePath) && !entries.Any(e => e.RelativePath == prevFile.RelativePath || e.PreviousRelativePath == prevFile.RelativePath))
                {
                    // Check if file still exists on disk but no longer claimed (stale content may still be there)
                    // We treat as stale regardless of current disk state: task no longer owns it
                    staleEntries.Add(new GeneratedFileManifestEntry(
                        RelativePath: prevFile.RelativePath,
                        ContentHash: prevFile.ContentHash,
                        ByteLength: prevFile.ByteLength,
                        Operation: "Stale",
                        BeforeHash: prevFile.ContentHash,
                        AfterHash: null,
                        BeforeByteLength: prevFile.ByteLength,
                        AfterByteLength: null));
                }
            }
            // Also check previous stale files that are still stale
            if (previousManifest.StaleFiles is not null)
            {
                foreach (var prevStale in previousManifest.StaleFiles)
                {
                    if (!currentPaths.Contains(prevStale.RelativePath) && !staleEntries.Any(e => e.RelativePath == prevStale.RelativePath))
                    {
                        staleEntries.Add(prevStale);
                    }
                }
            }
            staleEntries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));
        }

        entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));

        return new GeneratedFileManifest(
            SessionId: sessionId,
            WorkflowRunId: workflowRunId,
            StepKey: stepKey,
            StepRevision: stepRevision,
            WorkspaceHostPath: workspaceHostPath,
            WorkspaceCiPath: workspaceCiPath,
            TaskId: taskId,
            Files: entries,
            SkippedFiles: skippedFiles,
            CreatedAt: DateTimeOffset.UtcNow,
            ParentTaskId: parentTaskId,
            IsBuildRepair: isBuildRepair,
            BuildRepairCycle: buildRepairCycle,
            Model: model,
            ModelTier: modelTier,
            Usage: usage,
            Diagnostics: diagnostics,
            FinishReason: finishReason,
            StaleFiles: staleEntries.Count > 0 ? staleEntries : null);
    }

    public static async Task<GeneratedFileManifest> CreateAsync(
        string sessionId,
        string workflowRunId,
        string stepKey,
        int stepRevision,
        string workspaceHostPath,
        string workspaceCiPath,
        string taskId,
        IReadOnlyList<string> writtenFiles,
        IReadOnlyList<string> skippedFiles,
        string? parentTaskId = null,
        bool isBuildRepair = false,
        int buildRepairCycle = 0,
        string? model = null,
        int? modelTier = null,
        CodeGenerationUsage? usage = null,
        CodeGenerationDiagnostics? diagnostics = null,
        string? finishReason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceHostPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var entries = new List<GeneratedFileManifestEntry>(writtenFiles.Count);
        var workspaceFullPath = Path.GetFullPath(workspaceHostPath);

        foreach (var absolutePath in writtenFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(absolutePath);
            var relativePath = Path.GetRelativePath(workspaceFullPath, fullPath)
                .Replace('\\', '/');

            // Skip files outside workspace (should not happen after ToRelativePaths filtering)
            if (relativePath == ".." ||
                relativePath.StartsWith("../", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath))
            {
                relativePath = Path.GetFileName(fullPath);
            }

            string contentHash;
            long byteLength;
            if (File.Exists(fullPath))
            {
                var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                byteLength = bytes.Length;
            }
            else
            {
                contentHash = string.Empty;
                byteLength = 0;
            }

            entries.Add(new GeneratedFileManifestEntry(relativePath, contentHash, byteLength));
        }

        // Sort for deterministic hash
        entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));

        return new GeneratedFileManifest(
            SessionId: sessionId,
            WorkflowRunId: workflowRunId,
            StepKey: stepKey,
            StepRevision: stepRevision,
            WorkspaceHostPath: workspaceHostPath,
            WorkspaceCiPath: workspaceCiPath,
            TaskId: taskId,
            Files: entries,
            SkippedFiles: skippedFiles,
            CreatedAt: DateTimeOffset.UtcNow,
            ParentTaskId: parentTaskId,
            IsBuildRepair: isBuildRepair,
            BuildRepairCycle: buildRepairCycle,
            Model: model,
            ModelTier: modelTier,
            Usage: usage,
            Diagnostics: diagnostics,
            FinishReason: finishReason);
    }

    public static async Task<GeneratedFileManifest> CreateForScaffoldingAsync(
        string sessionId,
        string workflowRunId,
        string stepKey,
        int stepRevision,
        string workspaceHostPath,
        string workspaceCiPath,
        string taskId,
        IReadOnlyList<string> artifacts,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<GeneratedFileManifestEntry>(artifacts.Count);
        var workspaceFullPath = Path.GetFullPath(workspaceHostPath);

        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Artifacts from CI may be absolute or workspace-relative; normalize to absolute then relative
            var fullPath = Path.IsPathRooted(artifact)
                ? Path.GetFullPath(artifact)
                : Path.GetFullPath(Path.Combine(workspaceFullPath, artifact));
            var relativePath = Path.GetRelativePath(workspaceFullPath, fullPath)
                .Replace('\\', '/');
            if (relativePath == ".." ||
                relativePath.StartsWith("../", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath))
            {
                relativePath = artifact.Replace('\\', '/');
            }

            string contentHash;
            long byteLength;
            if (File.Exists(fullPath))
            {
                var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                byteLength = bytes.Length;
            }
            else
            {
                contentHash = string.Empty;
                byteLength = 0;
            }

            entries.Add(new GeneratedFileManifestEntry(relativePath, contentHash, byteLength));
        }

        entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));

        return new GeneratedFileManifest(
            SessionId: sessionId,
            WorkflowRunId: workflowRunId,
            StepKey: stepKey,
            StepRevision: stepRevision,
            WorkspaceHostPath: workspaceHostPath,
            WorkspaceCiPath: workspaceCiPath,
            TaskId: taskId,
            Files: entries,
            SkippedFiles: [],
            CreatedAt: DateTimeOffset.UtcNow);
    }
}
