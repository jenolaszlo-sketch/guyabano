using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.Llm.Prompting;
using Guyabano.Session;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Penghou.Cangjie;
using Penghou.Cangjie.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

public sealed class ContextSnapshotReferenceTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-snapshot-ref-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ArtifactReferencesExactCangjieSnapshot_ThatResolvesToOrderedSelection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sessionStore = new FileSystemGuyabanoSessionStore(Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: ct);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);

        var cangjieStore = new SqliteContextStore(new CangjieSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "cangjie.db"),
            Pooling = false
        });

        // Store ordered context items (the selection supplied to a planning call)
        var item1 = await cangjieStore.StoreAsync(new ContextItem
        {
            Scope = $"guyabano:session:{session.Id}",
            Key = "hetu:index-summary",
            Kind = ContextKinds.Summary,
            Content = "Repository indexed at revision abc.",
            Provenance = new ContextProvenance { Producer = "guyabano:test" },
            Tags = ["repository-context", $"session:{session.Id}"]
        }, new ContextWriteOptions { IdempotencyKey = "item-1" }, ct);
        var item2 = await cangjieStore.StoreAsync(new ContextItem
        {
            Scope = $"guyabano:session:{session.Id}",
            Key = "hetu:project/Sample.csproj",
            Kind = ContextKinds.Summary,
            Content = "Public surface of Sample.",
            Provenance = new ContextProvenance { Producer = "guyabano:test" },
            Tags = ["repository-context", $"session:{session.Id}"]
        }, new ContextWriteOptions { IdempotencyKey = "item-2" }, ct);

        var snapshot = await CangjieSnapshotHelper.EnsureSnapshotAsync(
            cangjieStore,
            session.Id.ToString(),
            runId.ToString("D"),
            "planning",
            stepRevision: 1,
            queryIdentity: $"guyabano:{runId:D}:repository-context",
            strategy: "hetu-public-surface-and-symbol-neighborhood",
            strategyVersion: "1",
            purpose: "code-generation-planning",
            workspaceRevision: "ws-rev-abc",
            hetuIndexRunId: "hetu-run-1",
            hetuIndexIdentity: "ws-rev-abc",
            itemIds: [item1.Id, item2.Id],
            cancellationToken: ct);

        var artifacts = new ZhinuPublishingArtifactRepository(
            new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts")),
            resolver);
        var workflow = new SnapshotReferencingWorkflow(artifacts, snapshot.Id, session.Id);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("snapshot-ref", "1", workflow));

        await engine.StartAsync("snapshot-ref", "1", "input", runId, cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var published = await engine.GetArtifactsAsync(runId, ct);
        var artifact = published.Should().ContainSingle(a => a.ArtifactType == "architecture").Subject;
        artifact.Metadata.Should().NotBeNull();
        artifact.Metadata!["cangjieSnapshotId"].Should().Be(snapshot.Id.ToString("D"));
        artifact.Metadata["cangjieStrategy"].Should().Be("hetu-public-surface-and-symbol-neighborhood");
        artifact.Metadata["cangjieStrategyVersion"].Should().Be("1");
        artifact.Metadata["cangjieQueryIdentity"].Should().Be($"guyabano:{runId:D}:repository-context");
        artifact.Metadata["hetuIndexRunId"].Should().Be("hetu-run-1");
        artifact.Metadata["workspaceRevision"].Should().Be("ws-rev-abc");

        // The snapshot must resolve to the exact ordered selection
        var resolved = await cangjieStore.ResolveSnapshotAsync(snapshot.Id, ct);
        resolved.Should().NotBeNull();
        resolved!.Snapshot.Id.Should().Be(snapshot.Id);
        resolved.Items.Select(i => i.Id).Should().Equal(new[] { item1.Id, item2.Id }, "ordered Cangjie selection must be preserved");
        resolved.Items.Select(i => i.Content).Should().Equal(
            "Repository indexed at revision abc.",
            "Public surface of Sample.");
        resolved.Snapshot.Strategy.Should().Be("hetu-public-surface-and-symbol-neighborhood");
        resolved.Snapshot.StrategyVersion.Should().Be("1");

        var reordered = await CangjieSnapshotHelper.EnsureSnapshotAsync(
            cangjieStore,
            session.Id.ToString(),
            runId.ToString("D"),
            "planning",
            stepRevision: 1,
            queryIdentity: $"guyabano:{runId:D}:repository-context",
            strategy: "hetu-public-surface-and-symbol-neighborhood",
            strategyVersion: "1",
            purpose: "code-generation-planning",
            workspaceRevision: "ws-rev-abc",
            hetuIndexRunId: "hetu-run-1",
            hetuIndexIdentity: "ws-rev-abc",
            itemIds: [item2.Id, item1.Id],
            cancellationToken: ct);
        var nextRevision = await CangjieSnapshotHelper.EnsureSnapshotAsync(
            cangjieStore,
            session.Id.ToString(),
            runId.ToString("D"),
            "planning",
            stepRevision: 2,
            queryIdentity: $"guyabano:{runId:D}:repository-context",
            strategy: "hetu-public-surface-and-symbol-neighborhood",
            strategyVersion: "1",
            purpose: "code-generation-planning",
            workspaceRevision: "ws-rev-abc",
            hetuIndexRunId: "hetu-run-1",
            hetuIndexIdentity: "ws-rev-abc",
            itemIds: [item1.Id, item2.Id],
            cancellationToken: ct);

        reordered.Id.Should().NotBe(snapshot.Id,
            "selection order is part of the exact disclosed context");
        nextRevision.Id.Should().NotBe(snapshot.Id,
            "a rerun is a distinct model invocation snapshot");

        var derived = await CangjieSnapshotHelper.DeriveSnapshotAsync(
            cangjieStore,
            snapshot.Id,
            session.Id.ToString(),
            runId.ToString("D"),
            "decomposition/1/TASK-TESTS",
            stepRevision: 1,
            queryIdentity: $"guyabano:{runId:D}:decomposition:TASK-TESTS",
            strategy: "decomposition-input-closure",
            strategyVersion: "2",
            purpose: "code-generation-decomposition",
            workspaceRevision: "ws-rev-abc",
            hetuIndexRunId: "hetu-run-1",
            hetuIndexIdentity: "ws-rev-abc",
            cancellationToken: ct);
        var resolvedDerived = await cangjieStore.ResolveSnapshotAsync(
            derived.Id,
            ct);

        derived.Id.Should().NotBe(snapshot.Id);
        derived.ItemIds.Should().Equal(item1.Id, item2.Id);
        resolvedDerived!.Items.Select(item => item.Id)
            .Should().Equal(item1.Id, item2.Id);
        derived.Purpose.Should().Be("code-generation-decomposition");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private sealed class SnapshotReferencingWorkflow(
        IArtifactRepository artifacts,
        Guid snapshotId,
        GuyabanoSessionId sessionId) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            await context.StepAsync("planning", input, async (value, step, token) =>
            {
                using var zhinuScope = CodeGenerationZhinuStepScope.Push(step);
                using var correlation = LlmRequestCorrelationScope.Push(new(
                    sessionId.ToString(),
                    context.WorkflowRunId.ToString("D"),
                    step.StepKey,
                    CangjieSnapshotId: snapshotId,
                    CangjieStrategy: "hetu-public-surface-and-symbol-neighborhood",
                    CangjieStrategyVersion: "1",
                    CangjieQueryIdentity: $"guyabano:{context.WorkflowRunId:D}:repository-context",
                    CangjiePurpose: "code-generation-planning",
                    HetuIndexRunId: "hetu-run-1",
                    HetuIndexIdentity: "ws-rev-abc",
                    WorkspaceRevision: "ws-rev-abc",
                    WorkflowStepRevision: step.Revision));
                await artifacts.WriteAsync(
                    new ArtifactWriteRequest<TestSnapshotArtifact>(
                        context.WorkflowRunId.ToString("D"),
                        "architecture",
                        1,
                        "architecture-v1",
                        ArtifactStatus.Validated,
                        new TestSnapshotArtifact(value)),
                    token);
                return value;
            }, cancellationToken: cancellationToken);
    }

    private sealed record TestSnapshotArtifact(string Value);
}
