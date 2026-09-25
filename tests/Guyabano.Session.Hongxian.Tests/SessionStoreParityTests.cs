using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Hongxian;
using Penghou.Hongxian.Sqlite;

namespace Guyabano.Session.Hongxian.Tests;

/// <summary>
/// Package-backed parity: the same scenario matrix runs against the duplicate
/// file-system kernel and the Hongxian-backed mapping layer. The duplicate is
/// removed only after the recovery scenario passes (roadmap item 5).
/// </summary>
public sealed class SessionStoreParityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-session-parity",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("filesystem")]
    [InlineData("hongxian")]
    public async Task Create_Get_round_trip(string backend)
    {
        using var opened = Open(backend, nameof(Create_Get_round_trip));
        var store = opened.Store;
        var created = await store.CreateAsync("repo:test", "workspace:test", cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await store.GetAsync(created.Id, TestContext.Current.CancellationToken);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(created.Id);
        loaded.RepositoryId.Should().Be("repo:test");
        loaded.WorkspaceId.Should().Be("workspace:test");
        loaded.WorkflowRunIds.Should().BeEmpty();
        loaded.CurrentWorkspaceRevision.Should().BeNull();
    }

    [Theory]
    [InlineData("filesystem")]
    [InlineData("hongxian")]
    public async Task List_contains_created_sessions(string backend)
    {
        using var opened = Open(backend, nameof(List_contains_created_sessions));
        var store = opened.Store;
        var first = await store.CreateAsync("repo:a", "workspace:a", cancellationToken: TestContext.Current.CancellationToken);
        var second = await store.CreateAsync("repo:b", "workspace:b", cancellationToken: TestContext.Current.CancellationToken);

        var listed = await store.ListAsync(TestContext.Current.CancellationToken);

        listed.Select(session => session.Id).Should().Contain([first.Id, second.Id]);
    }

    [Theory]
    [InlineData("filesystem")]
    [InlineData("hongxian")]
    public async Task Attach_and_find_workflow_run(string backend)
    {
        using var opened = Open(backend, nameof(Attach_and_find_workflow_run));
        var store = opened.Store;
        var created = await store.CreateAsync("repo:test", "workspace:test", cancellationToken: TestContext.Current.CancellationToken);
        var runId = Guid.NewGuid();

        var attached = await store.AttachWorkflowRunAsync(created.Id, runId, TestContext.Current.CancellationToken);
        attached.WorkflowRunIds.Should().ContainSingle().Which.Should().Be(runId);

        var found = await store.FindByWorkflowRunAsync(runId, TestContext.Current.CancellationToken);
        found.Should().NotBeNull();
        found!.Id.Should().Be(created.Id);

        var missing = await store.FindByWorkflowRunAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        missing.Should().BeNull();
    }

    [Theory]
    [InlineData("filesystem")]
    [InlineData("hongxian")]
    public async Task Revision_compare_and_swap_conflicts(string backend)
    {
        using var opened = Open(backend, nameof(Revision_compare_and_swap_conflicts));
        var store = opened.Store;
        var created = await store.CreateAsync("repo:test", "workspace:test", cancellationToken: TestContext.Current.CancellationToken);

        var promoted = await store.UpdateWorkspaceRevisionAsync(
            created.Id, null, "revision-1", TestContext.Current.CancellationToken);
        promoted.Should().NotBeNull();
        promoted!.CurrentWorkspaceRevision.Should().Be("revision-1");

        var conflict = await store.UpdateWorkspaceRevisionAsync(
            created.Id, null, "revision-2", TestContext.Current.CancellationToken);
        conflict.Should().BeNull();

        var loaded = await store.GetAsync(created.Id, TestContext.Current.CancellationToken);
        loaded!.CurrentWorkspaceRevision.Should().Be("revision-1");
    }

    [Theory]
    [InlineData("filesystem")]
    [InlineData("hongxian")]
    public async Task Reopen_preserves_sessions_and_runs(string backend)
    {
        var directory = Path.Combine(root, backend, nameof(Reopen_preserves_sessions_and_runs));
        var runId = Guid.NewGuid();
        GuyabanoSessionId id;
        using (var first = Open(backend, directory))
        {
            var created = await first.Store.CreateAsync("repo:test", "workspace:test", cancellationToken: TestContext.Current.CancellationToken);
            id = created.Id;
            await first.Store.AttachWorkflowRunAsync(id, runId, TestContext.Current.CancellationToken);
            await first.Store.UpdateWorkspaceRevisionAsync(id, null, "revision-1", TestContext.Current.CancellationToken);
        }

        using (var second = Open(backend, directory))
        {
            var loaded = await second.Store.GetAsync(id, TestContext.Current.CancellationToken);
            loaded.Should().NotBeNull();
            loaded!.WorkflowRunIds.Should().ContainSingle().Which.Should().Be(runId);
            loaded.CurrentWorkspaceRevision.Should().Be("revision-1");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private OpenedStore Open(string backend, string name) =>
        OpenAt(backend, Path.Combine(root, backend, name));

    private OpenedStore OpenAt(string backend, string directory)
    {
        if (backend == "filesystem")
        {
            var filesystem = new FileSystemGuyabanoSessionStore(directory);
            return new OpenedStore(filesystem, filesystem);
        }

        var set = new HongxianSqliteStoreSet(new HongxianSqliteOptions { RootPath = directory, Pooling = false });
        return new OpenedStore(new HongxianGuyabanoSessionStore(set.SessionStore), set);
    }

    private sealed class OpenedStore : IDisposable
    {
        private readonly IGuyabanoSessionStore store;
        private readonly IDisposable? lifetime;

        public OpenedStore(IGuyabanoSessionStore store, IDisposable? lifetime = null)
        {
            this.store = store;
            this.lifetime = lifetime;
        }

        public IGuyabanoSessionStore Store => store;

        public void Dispose() => lifetime?.Dispose();
    }
}
