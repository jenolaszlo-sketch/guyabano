using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Session;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Penghou.Cangjie;
using Penghou.Cangjie.Sqlite;
using Penghou.Hetu;
using Penghou.Hetu.CSharp;

namespace Guyabano.WorkflowProgressTests;

public sealed class RepositoryReindexTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-reindex-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Reindex_IncrementalUnchangedSourcesAreSkipped_AndPublicationPersisted()
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
        await File.WriteAllTextAsync(
            Path.Combine(workspace.HostPath, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.HostPath, "CustomerService.cs"),
            "namespace Sample; public sealed class CustomerService { public void Run() { } }",
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.HostPath, "OrderService.cs"),
            "namespace Sample; public sealed class OrderService { public void Ship() { } }",
            ct);

        await using var hetu = new HetuHostBuilder().AddCSharpPlugin().Build();
        var contextStore = new SqliteContextStore(new CangjieSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "cangjie.db"),
            Pooling = false
        });
        var artifactRepository = new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts"));
        var reindexer = new CodeGenerationRepositoryReindexer(
            hetu,
            contextStore,
            artifactRepository,
            resolver,
            Options.Create(new CodeGenerationWorkerOptions
            {
                OutputRoot = rootPath,
                RepositoryContextEnabled = true,
                RepositoryId = "repo:test"
            }));

        var request = new RepositoryReindexRequest(runId.ToString("D"));
        RepositoryReindexReceipt first;
        using (CodeGenerationZhinuStepScope.Push(new Penghou.Zhinu.WorkflowStepContext(runId, Guid.NewGuid(), "repository/reindex-post-generation", attempt: 1, revision: 0, isCompensation: false)))
            first = await reindexer.ReindexAsync(request, ct);
        first.FilesDiscovered.Should().BeGreaterThanOrEqualTo(3);
        first.FilesNew.Should().Be(3, "first reindex creates all source units");
        first.NodesProduced.Should().BeGreaterThan(0);

        // Second reindex of unchanged workspace must skip all sources
        RepositoryReindexReceipt second;
        using (CodeGenerationZhinuStepScope.Push(new Penghou.Zhinu.WorkflowStepContext(runId, Guid.NewGuid(), "repository/reindex-post-generation", attempt: 1, revision: 1, isCompensation: false)))
            second = await reindexer.ReindexAsync(request, ct);
        second.FilesNew.Should().Be(0, "unchanged sources must not be reindexed");
        second.FilesChanged.Should().Be(0, "unchanged sources must not be reindexed");
        second.FilesUnchanged.Should().BeGreaterThanOrEqualTo(3, "all unchanged sources are skipped");
        second.FilesDeleted.Should().Be(0);
        second.IndexIdentity.Should().Be(first.IndexIdentity, "deterministic index identity for identical content");
        second.IndexRunId.Should().NotBe(first.IndexRunId);

        // Repository publication v2 persisted as authoritative artifact
        var publication = await artifactRepository.ReadLatestAsync<RepositoryReindexPublicationPayload>(
            runId.ToString("D"),
            "repository-publication",
            "post-generation",
            ct);
        publication.Should().NotBeNull();
        var workspaceSnapshot = await GeneratedFileManifestFactory
            .SnapshotWorkspaceAsync(workspace.HostPath, ct);
        var canonicalWorkspace = string.Join(
            "|",
            workspaceSnapshot.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value.Hash}"));
        var expectedWorkspaceRevision = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(canonicalWorkspace)))
            .ToLowerInvariant();
        publication!.Payload.WorkspaceRevisionId.Should()
            .Be(expectedWorkspaceRevision);
        publication.Payload.IndexIdentity.Should().Be(second.IndexIdentity);
        publication.Payload.IndexRunId.Should().Be(second.IndexRunId);
        publication.Payload.SessionId.Should().Be(session.Id.ToString());
        publication.Payload.FilesUnchanged.Should().Be(second.FilesUnchanged);

        // Compact Cangjie summary linked to publication
        var summary = await contextStore.GetLatestByKeyAsync(
            $"guyabano:session:{session.Id}",
            $"publication:{second.IndexRunId}",
            ct);
        summary.Should().NotBeNull();
        summary!.Kind.Should().Be(ContextKinds.Summary);
        summary.Content.Should().Contain(second.IndexIdentity);
        summary.Metadata["indexRunId"].Should().Be(second.IndexRunId);
    }

    [Fact]
    public async Task Reindex_AfterMutation_PicksUpChangedAndDeletesRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionStore = new FileSystemGuyabanoSessionStore(Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:mutating", "workspace:test", cancellationToken: ct);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);
        var projectPath = Path.Combine(workspace.HostPath, "Sample.csproj");
        var sourcePath = Path.Combine(workspace.HostPath, "CustomerService.cs");
        var removedPath = Path.Combine(workspace.HostPath, "LegacyService.cs");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", ct);
        await File.WriteAllTextAsync(sourcePath, "namespace Sample; public sealed class CustomerService { }", ct);
        await File.WriteAllTextAsync(removedPath, "namespace Sample; public sealed class LegacyService { }", ct);

        await using var hetu = new HetuHostBuilder().AddCSharpPlugin().Build();
        var contextStore = new SqliteContextStore(new CangjieSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "cangjie-mutate.db"),
            Pooling = false
        });
        var artifactRepository = new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts-mutate"));
        var reindexer = new CodeGenerationRepositoryReindexer(
            hetu,
            contextStore,
            artifactRepository,
            resolver,
            Options.Create(new CodeGenerationWorkerOptions
            {
                OutputRoot = rootPath,
                RepositoryContextEnabled = true,
                RepositoryId = "repo:mutating"
            }));

        var request = new RepositoryReindexRequest(runId.ToString("D"));
        RepositoryReindexReceipt before;
        using (CodeGenerationZhinuStepScope.Push(new Penghou.Zhinu.WorkflowStepContext(runId, Guid.NewGuid(), "repository/reindex-post-generation", attempt: 1, revision: 0, isCompensation: false)))
            before = await reindexer.ReindexAsync(request, ct);

        // Mutate: change source, delete legacy
        await File.WriteAllTextAsync(sourcePath, "namespace Sample; public sealed class CustomerService { public void Run() { } }", ct);
        File.Delete(removedPath);

        RepositoryReindexReceipt after;
        using (CodeGenerationZhinuStepScope.Push(new Penghou.Zhinu.WorkflowStepContext(runId, Guid.NewGuid(), "repository/reindex-post-generation", attempt: 1, revision: 1, isCompensation: false)))
            after = await reindexer.ReindexAsync(request, ct);
        after.FilesChanged.Should().Be(1, "mutated source is reindexed");
        after.FilesDeleted.Should().Be(1, "removed source is deleted");
        after.IndexIdentity.Should().NotBe(before.IndexIdentity, "changed content produces new index identity");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
