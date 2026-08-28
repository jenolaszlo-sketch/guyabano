using FluentAssertions;
using System.Text.Json.Nodes;

namespace Guyabano.Artifacts.Tests;

public sealed class FileSystemArtifactRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-artifacts-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteAndReadAsync_PreservesValidatedArtifact()
    {
        var repository = new FileSystemArtifactRepository(_root);
        var request = new ArtifactWriteRequest<TestPayload>(
            "workflow-1",
            "decomposition",
            1,
            "T-Store",
            ArtifactStatus.Validated,
            new TestPayload("T-Store", ["Store.cs"]))
        {
            SessionId = "session-1"
        };

        var cancellationToken = TestContext.Current.CancellationToken;
        var written = await repository.WriteAsync(
            request,
            cancellationToken);
        var loaded = await repository.ReadAsync<TestPayload>(
            written.Reference,
            cancellationToken);

        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(ArtifactStatus.Validated);
        loaded.SessionId.Should().Be("session-1");
        loaded.Payload.Should().BeEquivalentTo(request.Payload);
        loaded.Reference.HashVersion.Should().Be(
            CanonicalJsonContentHash.Version);
        File.Exists(Path.Combine(
            _root,
            written.Reference.RelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar))).Should().BeTrue();
    }

    [Fact]
    public void CanonicalHash_IsIndependentOfObjectPropertyOrderAndNumberSpelling()
    {
        using var first = System.Text.Json.JsonDocument.Parse(
            "{\"z\":1.0,\"a\":{\"b\":2,\"a\":true}}");
        using var second = System.Text.Json.JsonDocument.Parse(
            "{\"a\":{\"a\":true,\"b\":2.00},\"z\":1}");

        CanonicalJsonContentHash.Compute(first.RootElement).Should().Be(
            CanonicalJsonContentHash.Compute(second.RootElement));
    }

    [Fact]
    public async Task WriteAsync_IsIdempotentForSameContent()
    {
        var repository = new FileSystemArtifactRepository(_root);
        var request = new ArtifactWriteRequest<TestPayload>(
            "workflow-1",
            "decomposition",
            1,
            "T-Store",
            ArtifactStatus.Validated,
            new TestPayload("T-Store", ["Store.cs"]));

        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await repository.WriteAsync(
            request,
            cancellationToken);
        var second = await repository.WriteAsync(
            request,
            cancellationToken);

        second.Reference.Should().Be(first.Reference);
        second.CreatedAt.Should().Be(first.CreatedAt);
    }

    [Fact]
    public async Task WriteAsync_UsesDifferentIdentityForApprovalState()
    {
        var repository = new FileSystemArtifactRepository(_root);
        var payload = new TestPayload("T-Store", ["Store.cs"]);
        var cancellationToken = TestContext.Current.CancellationToken;

        var validated = await repository.WriteAsync(
            new ArtifactWriteRequest<TestPayload>(
                "workflow-1",
                "decomposition",
                1,
                "T-Store",
                ArtifactStatus.Validated,
                payload),
            cancellationToken);
        var approved = await repository.WriteAsync(
            new ArtifactWriteRequest<TestPayload>(
                "workflow-1",
                "decomposition",
                1,
                "T-Store",
                ArtifactStatus.Approved,
                payload),
            cancellationToken);

        approved.Reference.ArtifactId
            .Should().NotBe(validated.Reference.ArtifactId);
    }

    [Fact]
    public async Task ReadAsync_RejectsTamperedPayload()
    {
        var repository = new FileSystemArtifactRepository(_root);
        var cancellationToken = TestContext.Current.CancellationToken;
        var written = await repository.WriteAsync(
            new ArtifactWriteRequest<TestPayload>(
                "workflow-1",
                "decomposition",
                1,
                "T-Store",
                ArtifactStatus.Validated,
                new TestPayload("T-Store", ["Store.cs"])),
            cancellationToken);
        var path = Path.Combine(
            _root,
            written.Reference.RelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar));
        var node = JsonNode.Parse(await File.ReadAllTextAsync(
            path,
            cancellationToken))!;
        node["payload"]!["parentTaskId"] = "T-Tampered";
        await File.WriteAllTextAsync(
            path,
            node.ToJsonString(),
            cancellationToken);

        var action = () => repository.ReadAsync<TestPayload>(
            written.Reference,
            cancellationToken);

        await action.Should().ThrowAsync<ArtifactIntegrityException>()
            .WithMessage("*content hash*");
    }

    [Fact]
    public async Task ReadAsync_RejectsPathTraversal()
    {
        var repository = new FileSystemArtifactRepository(_root);
        var reference = new ArtifactReference(
            "bad",
            "decomposition",
            1,
            "../outside.json",
            "bad");

        var action = () => repository.ReadAsync<TestPayload>(
            reference,
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<ArtifactIntegrityException>()
            .WithMessage("*escapes*");
    }

    [Fact]
    public async Task ReadLatestAsync_ReturnsNewestCheckpointForStage()
    {
        var repository = new FileSystemArtifactRepository(_root);
        var cancellationToken = TestContext.Current.CancellationToken;
        await repository.WriteAsync(
            new ArtifactWriteRequest<TestPayload>(
                "workflow-1",
                "workflow-checkpoint",
                1,
                "latest",
                ArtifactStatus.Validated,
                new TestPayload("first", ["First.cs"])),
            cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        await repository.WriteAsync(
            new ArtifactWriteRequest<TestPayload>(
                "workflow-1",
                "workflow-checkpoint",
                1,
                "latest",
                ArtifactStatus.Validated,
                new TestPayload("second", ["Second.cs"])),
            cancellationToken);

        var latest = await repository.ReadLatestAsync<TestPayload>(
            "workflow-1",
            "workflow-checkpoint",
            "latest",
            cancellationToken);

        latest.Should().NotBeNull();
        latest!.Payload.ParentTaskId.Should().Be("second");
    }

    [Fact]
    public async Task ReadLatestAsync_ReturnsNullWhenStageDoesNotExist()
    {
        var repository = new FileSystemArtifactRepository(_root);

        var latest = await repository.ReadLatestAsync<TestPayload>(
            "missing-workflow",
            "workflow-checkpoint",
            "latest",
            TestContext.Current.CancellationToken);

        latest.Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record TestPayload(
        string ParentTaskId,
        IReadOnlyList<string> Paths);
}
