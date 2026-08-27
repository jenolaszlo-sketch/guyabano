using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Cangjie;
using Penghou.Cangjie.Sqlite;

namespace Guyabano.Artifacts.Tests;

public sealed class ContextIndexingArtifactRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-artifact-context-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteAsync_IndexesArtifactsAndInputProvenance()
    {
        var contextStore = CreateContextStore();
        var repository = new ContextIndexingArtifactRepository(
            new FileSystemArtifactRepository(Path.Combine(root, "artifacts")),
            contextStore);
        var cancellationToken = TestContext.Current.CancellationToken;
        var input = await repository.WriteAsync(
            CreateRequest("domain-discovery", "discover", "input"),
            cancellationToken);
        var output = await repository.WriteAsync(
            CreateRequest(
                "solution-topology",
                "topology",
                "output",
                [input.Reference]),
            cancellationToken);

        var indexed = await contextStore.SearchAsync(
            new ContextQuery
            {
                Scope = ContextIndexingArtifactRepository.DefaultScope,
                Key = output.Reference.ArtifactId,
                Limit = 1
            },
            cancellationToken);

        indexed.Should().ContainSingle();
        indexed[0].Item.Metadata["workflowId"].Should().Be("workflow-1");
        indexed[0].Item.Metadata["sessionId"].Should().Be("session-1");
        indexed[0].Item.Tags.Should().Contain("session:session-1");
        indexed[0].Item.Content.Should().Contain("output");
        var relations = await contextStore.GetRelationsAsync(
            indexed[0].Item.Id,
            cancellationToken: cancellationToken);
        relations.Should().ContainSingle(relation =>
            relation.Kind == ContextRelationKinds.DerivedFrom);
    }

    [Fact]
    public async Task WriteAsync_IsIdempotentInTheContextIndex()
    {
        var contextStore = CreateContextStore();
        var repository = new ContextIndexingArtifactRepository(
            new FileSystemArtifactRepository(Path.Combine(root, "artifacts")),
            contextStore);
        var request = CreateRequest("domain-discovery", "discover", "same");
        var cancellationToken = TestContext.Current.CancellationToken;

        await repository.WriteAsync(request, cancellationToken);
        await repository.WriteAsync(request, cancellationToken);

        var indexed = await contextStore.SearchAsync(
            new ContextQuery
            {
                Scope = ContextIndexingArtifactRepository.DefaultScope,
                Tags = ["workflow:workflow-1"],
                Limit = 10
            },
            cancellationToken);
        indexed.Should().ContainSingle();
    }

    [Fact]
    public async Task WriteAsync_LinksAChangedStageToItsPreviousRevision()
    {
        var contextStore = CreateContextStore();
        var repository = new ContextIndexingArtifactRepository(
            new FileSystemArtifactRepository(Path.Combine(root, "artifacts")),
            contextStore);
        var cancellationToken = TestContext.Current.CancellationToken;
        await repository.WriteAsync(
            CreateRequest("architecture", "review", "first"),
            cancellationToken);
        var revised = await repository.WriteAsync(
            CreateRequest("architecture", "review", "revised"),
            cancellationToken);
        var indexed = await contextStore.SearchAsync(
            new ContextQuery
            {
                Scope = ContextIndexingArtifactRepository.DefaultScope,
                Key = revised.Reference.ArtifactId,
                Limit = 1
            },
            cancellationToken);

        var relations = await contextStore.GetRelationsAsync(
            indexed.Single().Item.Id,
            cancellationToken: cancellationToken);
        relations.Should().ContainSingle(relation =>
            relation.Kind == ContextRelationKinds.Supersedes);
    }

    private SqliteContextStore CreateContextStore() =>
        new(new CangjieSqliteOptions
        {
            DatabasePath = Path.Combine(root, "cangjie.db")
        });

    private static ArtifactWriteRequest<TestPayload> CreateRequest(
        string kind,
        string stage,
        string value,
        IReadOnlyList<ArtifactReference>? inputs = null) =>
        new(
            "workflow-1",
            kind,
            1,
            stage,
            ArtifactStatus.Validated,
            new TestPayload(value),
            inputs)
        {
            SessionId = "session-1"
        };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed record TestPayload(string Value);
}
