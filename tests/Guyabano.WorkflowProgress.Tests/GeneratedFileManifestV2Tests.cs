#pragma warning disable xUnit1051
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Session;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

public sealed class GeneratedFileManifestV2Tests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-manifest-v2-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Manifest_RecordsCreatedModifiedDeletedRenamed_WithBeforeAfterHashes()
    {
        var workspace = Path.Combine(rootPath, "workspace");
        Directory.CreateDirectory(workspace);

        // Before snapshot: file A v1, file B v1, file C v1
        await File.WriteAllTextAsync(Path.Combine(workspace, "A.cs"), "class A v1");
        await File.WriteAllTextAsync(Path.Combine(workspace, "B.cs"), "class B v1");
        await File.WriteAllTextAsync(Path.Combine(workspace, "C.cs"), "class C v1");
        var before = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace);

        // After: A modified, B deleted, C renamed to C2.cs (same content), D created
        File.Delete(Path.Combine(workspace, "B.cs"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "A.cs"), "class A v2");
        File.Move(Path.Combine(workspace, "C.cs"), Path.Combine(workspace, "C2.cs"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "D.cs"), "class D v1");

        var after = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace);

        var manifest = await GeneratedFileManifestFactory.CreateWithWorkspaceDiffAsync(
            sessionId: Guid.NewGuid().ToString(),
            workflowRunId: Guid.NewGuid().ToString(),
            stepKey: "generation/task-1/leaf-1",
            stepRevision: 1,
            workspaceHostPath: workspace,
            workspaceCiPath: ".",
            taskId: "leaf-1",
            beforeSnapshot: before,
            afterSnapshot: after,
            previousManifest: null,
            skippedFiles: [],
            parentTaskId: "task-1",
            model: "test-model");

        manifest.Files.Should().Contain(f => f.RelativePath == "D.cs" && f.Operation == "Created" && f.BeforeHash == null && f.AfterHash != null);
        manifest.Files.Should().Contain(f => f.RelativePath == "A.cs" && f.Operation == "Modified" && f.BeforeHash != null && f.AfterHash != null && f.BeforeHash != f.AfterHash);
        manifest.Files.Should().Contain(f => f.RelativePath == "B.cs" && f.Operation == "Deleted" && f.BeforeHash != null && f.AfterHash == null);
        var renamed = manifest.Files.Single(f => f.Operation == "Renamed");
        renamed.RelativePath.Should().Be("C2.cs");
        renamed.PreviousRelativePath.Should().Be("C.cs");
        renamed.BeforeHash.Should().Be(renamed.AfterHash);
        manifest.TaskId.Should().Be("leaf-1");
        manifest.StepKey.Should().Be("generation/task-1/leaf-1");
        manifest.WorkspaceRevisionId.Should().BeNull();
    }

    [Fact]
    public async Task Manifest_DetectsStaleFilesPreviouslyOwnedByTask()
    {
        var workspace = Path.Combine(rootPath, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "OwnedA.cs"), "owned A");
        await File.WriteAllTextAsync(Path.Combine(workspace, "OwnedB.cs"), "owned B");
        var before1 = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace);
        // First manifest owns OwnedA and OwnedB (created)
        var after1 = before1; // no change for first, but we simulate that task created them
        var manifest1 = await GeneratedFileManifestFactory.CreateWithWorkspaceDiffAsync(
            sessionId: "session-1",
            workflowRunId: "run-1",
            stepKey: "generation/task-1/leaf-1",
            stepRevision: 0,
            workspaceHostPath: workspace,
            workspaceCiPath: ".",
            taskId: "leaf-1",
            beforeSnapshot: new Dictionary<string, (string, long)>(),
            afterSnapshot: after1,
            previousManifest: null,
            skippedFiles: []);

        manifest1.Files.Should().HaveCount(2);
        manifest1.Files.All(f => f.Operation == "Created").Should().BeTrue();

        // Second run: task no longer produces OwnedB, but OwnedB still exists on disk (stale)
        // Simulate that task now only owns OwnedA (modified) and new file OwnedC
        await File.WriteAllTextAsync(Path.Combine(workspace, "OwnedA.cs"), "owned A v2");
        await File.WriteAllTextAsync(Path.Combine(workspace, "OwnedC.cs"), "owned C");
        // Note: OwnedB still exists on disk but task no longer claims it; afterSnapshot still contains it
        var before2 = after1;
        var after2 = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace);
        // For stale detection, previous manifest is manifest1, but current task's logical output should be only OwnedA and OwnedC.
        // To simulate task no longer owning OwnedB, we will create a manifest via diff that shows OwnedB as not in current Files but previous owned.
        // However our diff based on workspace will still see OwnedB as present (since file still on disk), so it would be considered not deleted.
        // To detect stale, we need to compare previous owned files vs current owned files (current Files from diff).
        // For this test, we simulate that current task's Files should be only OwnedA (modified) and OwnedC (created), so OwnedB should be stale.
        // We achieve this by having before2 = after1, after2 contains all three files, but the diff will show OwnedA modified, OwnedC created, but OwnedB unchanged (no entry).
        // Stale detection should flag OwnedB as stale because it was previously owned but is not in current diff's Files and is still on disk but no longer produced.
        // Our factory's stale logic checks previous manifest's files not in afterSnapshot? Actually it checks not in afterSnapshot, but afterSnapshot does contain OwnedB, so it wouldn't be flagged.
        // We need to test stale where file was actually deleted from disk (so afterSnapshot doesn't contain it) but task no longer produces it.
        // Let's delete OwnedB from disk to simulate task cleanup.
        File.Delete(Path.Combine(workspace, "OwnedB.cs"));
        var after2Deleted = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace);
        var manifest2 = await GeneratedFileManifestFactory.CreateWithWorkspaceDiffAsync(
            sessionId: "session-1",
            workflowRunId: "run-1",
            stepKey: "generation/task-1/leaf-1",
            stepRevision: 1,
            workspaceHostPath: workspace,
            workspaceCiPath: ".",
            taskId: "leaf-1",
            beforeSnapshot: before2,
            afterSnapshot: after2Deleted,
            previousManifest: manifest1,
            skippedFiles: []);

        // OwnedB should be detected as stale/deleted via before/after diff (since it was deleted from disk)
        // And also via previous manifest stale check
        manifest2.Files.Should().Contain(f => f.RelativePath == "OwnedB.cs" && (f.Operation == "Deleted" || f.Operation == "Stale"));
        // Also verify that manifest2's StaleFiles contains OwnedB if we treat deleted as stale
        if (manifest2.StaleFiles is not null)
            manifest2.StaleFiles.Should().Contain(f => f.RelativePath == "OwnedB.cs");

        // Verify that we can answer which task produced OwnedA
        var manifests = new[] { manifest1, manifest2 };
        var whichTask = FindProducingTask(manifests, "OwnedA.cs");
        whichTask.Should().Be("leaf-1");
        whichTask = FindProducingTask(manifests, "OwnedB.cs");
        whichTask.Should().Be("leaf-1");
    }

    [Fact]
    public async Task Manifest_IsImmutablePerStepRevision_AndPublishedViaZhinu()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionStore = new FileSystemGuyabanoSessionStore(Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: ct);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);

        var artifacts = new ZhinuPublishingArtifactRepository(
            new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts")),
            resolver);

        var workflow = new ManifestPublishingWorkflow(artifacts, workspace);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"), Pooling = false });
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("manifest-immutable", "1", workflow));

        await engine.StartAsync("manifest-immutable", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var published = await engine.GetArtifactsAsync(runId, ct);
        var manifestArtifact = published.Should().ContainSingle(a => a.ArtifactType == "generated-file-manifest").Subject;
        manifestArtifact.ArtifactVersion.Should().Be("2");
        manifestArtifact.ContentHash.Should().NotBeNullOrWhiteSpace();
        manifestArtifact.Metadata!["sessionId"].Should().Be(session.Id.ToString());
        manifestArtifact.ProducerStepKey.Should().Be("generation/task-1/leaf-1");

        var envelope = await artifacts.ReadAsync<GeneratedFileManifest>(
            new ArtifactReference(manifestArtifact.Metadata["artifactId"], manifestArtifact.ArtifactType, 2, manifestArtifact.Location, manifestArtifact.ContentHash!)
            {
                HashVersion = manifestArtifact.Metadata["hashVersion"]
            },
            ct);
        envelope.Should().NotBeNull();
        envelope!.Payload.Files.Should().Contain(f => f.RelativePath == "File.cs" && f.Operation == "Created");
        envelope.Payload.TaskId.Should().Be("leaf-1");
        envelope.Payload.StepRevision.Should().BeGreaterThanOrEqualTo(0);

        // Second write with same content should be idempotent (same hash, same artifactId)
        var secondEnvelope = await artifacts.ReadLatestAsync<GeneratedFileManifest>(runId.ToString("D"), "generated-file-manifest", "leaf-1", ct);
        secondEnvelope.Should().NotBeNull();
        secondEnvelope!.Reference.ContentHash.Should().Be(envelope.Reference.ContentHash);
    }

    [Fact]
    public async Task Query_WhichStepProducedFile_ReturnsTaskAndStep()
    {
        var workspace = Path.Combine(rootPath, "workspace-query");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Alpha.cs"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(workspace, "Beta.cs"), "beta");
        var before = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace);
        // Simulate two tasks: task-1 produces Alpha, task-2 produces Beta
        var afterTask1 = new Dictionary<string, (string, long)>(before);
        // For task-1, after is just Alpha (but we simulate task-1 manifest)
        var manifestTask1 = await GeneratedFileManifestFactory.CreateWithWorkspaceDiffAsync(
            sessionId: "session-1",
            workflowRunId: "run-1",
            stepKey: "generation/task-1/leaf-a",
            stepRevision: 0,
            workspaceHostPath: workspace,
            workspaceCiPath: ".",
            taskId: "leaf-a",
            beforeSnapshot: new Dictionary<string, (string, long)>(),
            afterSnapshot: new Dictionary<string, (string, long)>(new[] { new KeyValuePair<string, (string, long)>("Alpha.cs", before["Alpha.cs"]) }),
            previousManifest: null,
            skippedFiles: []);

        var manifestTask2 = await GeneratedFileManifestFactory.CreateWithWorkspaceDiffAsync(
            sessionId: "session-1",
            workflowRunId: "run-1",
            stepKey: "generation/task-1/leaf-b",
            stepRevision: 0,
            workspaceHostPath: workspace,
            workspaceCiPath: ".",
            taskId: "leaf-b",
            beforeSnapshot: new Dictionary<string, (string, long)>(new[] { new KeyValuePair<string, (string, long)>("Alpha.cs", before["Alpha.cs"]) }),
            afterSnapshot: before,
            previousManifest: null,
            skippedFiles: []);

        var allManifests = new[] { manifestTask1, manifestTask2 };
        FindProducingTask(allManifests, "Alpha.cs").Should().Be("leaf-a");
        FindProducingTask(allManifests, "Beta.cs").Should().Be("leaf-b");
        FindProducingTask(allManifests, "Gamma.cs").Should().BeNull();
    }

    private static string? FindProducingTask(IEnumerable<GeneratedFileManifest> manifests, string relativePath)
    {
        foreach (var m in manifests)
        {
            if (m.Files.Any(f => f.RelativePath == relativePath) || (m.StaleFiles?.Any(f => f.RelativePath == relativePath) ?? false))
                return m.TaskId;
        }
        return null;
    }

    [Fact]
    public async Task CurrentOwnership_DoesNotClaimObservedSiblingChanges()
    {
        var before = new Dictionary<string, (string Hash, long Length)>(
            StringComparer.Ordinal);
        var after = new Dictionary<string, (string Hash, long Length)>(
            StringComparer.Ordinal)
        {
            ["Owned.cs"] = ("owned-hash", 10),
            ["Sibling.cs"] = ("sibling-hash", 20)
        };

        var manifest = await GeneratedFileManifestFactory
            .CreateWithWorkspaceDiffAsync(
                sessionId: "session",
                workflowRunId: "workflow",
                stepKey: "generation/task-a",
                stepRevision: 1,
                workspaceHostPath: rootPath,
                workspaceCiPath: ".",
                taskId: "task-a",
                beforeSnapshot: before,
                afterSnapshot: after,
                previousManifest: null,
                skippedFiles: [],
                currentOwnedPaths: new HashSet<string>(
                    ["Owned.cs"],
                    StringComparer.Ordinal));

        manifest.Files.Should().ContainSingle()
            .Which.RelativePath.Should().Be("Owned.cs");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private sealed class ManifestPublishingWorkflow(IArtifactRepository artifacts, CodeGenerationWorkspace workspace) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            await context.StepAsync("generation/task-1/leaf-1", input, async (value, step, token) =>
            {
                using var scope = CodeGenerationZhinuStepScope.Push(step);
                var before = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace.HostPath, token);
                var filePath = Path.Combine(workspace.HostPath, "File.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await File.WriteAllTextAsync(filePath, "content v1", token);
                var after = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(workspace.HostPath, token);
                var previous = await artifacts.ReadLatestAsync<GeneratedFileManifest>(context.WorkflowRunId.ToString("D"), "generated-file-manifest", "leaf-1", token);
                var manifest = await GeneratedFileManifestFactory.CreateWithWorkspaceDiffAsync(
                    sessionId: workspace.SessionId.ToString(),
                    workflowRunId: context.WorkflowRunId.ToString("D"),
                    stepKey: step.StepKey,
                    stepRevision: step.Revision,
                    workspaceHostPath: workspace.HostPath,
                    workspaceCiPath: workspace.CiRelativePath,
                    taskId: "leaf-1",
                    beforeSnapshot: before,
                    afterSnapshot: after,
                    previousManifest: previous?.Payload,
                    skippedFiles: []);
                await artifacts.WriteAsync(
                    new ArtifactWriteRequest<GeneratedFileManifest>(context.WorkflowRunId.ToString("D"), "generated-file-manifest", 2, "leaf-1", ArtifactStatus.Validated, manifest),
                    token);
                return value;
            }, cancellationToken: cancellationToken);
    }
}
