using System.Diagnostics;
using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;

namespace Guyabano.SessionTests;

public sealed class SessionCheckpointAnchoringTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(), "guyabano-anchoring-tests", Guid.NewGuid().ToString("N"));
    private readonly string repoPath = Path.Combine(
        Path.GetTempPath(), "guyabano-anchoring-repo", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Anchor_RoundTripsAndVerifies()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow,
            PayloadJson: "{\"prompt\":\"hello\"}"), ct);
        InitRepo();

        var anchor = await SimingSessionCheckpointAnchoring.AnchorAsync(
            store, sessionId, repoPath, "checkpoint", ct);

        anchor.SessionId.Should().Be(sessionId);
        anchor.CommitSha.Should().MatchRegex("^[0-9a-f]{40}$");
        var result = await SimingSessionCheckpointAnchoring.VerifyAnchoredAsync(
            store, anchor, repoPath, ct);
        result.IsValid.Should().BeTrue();
        result.VerifiedEntries.Should().Be(1);
    }

    [Fact]
    public async Task Anchor_RemainsValidAfterFurtherAppends()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
        InitRepo();
        var anchor = await SimingSessionCheckpointAnchoring.AnchorAsync(
            store, sessionId, repoPath, "checkpoint", ct);

        await store.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.WorkflowStarted, DateTimeOffset.UtcNow), ct);

        var result = await SimingSessionCheckpointAnchoring.VerifyAnchoredAsync(
            store, anchor, repoPath, ct);
        result.IsValid.Should().BeTrue();
        result.VerifiedEntries.Should().Be(2);
    }

    [Fact]
    public async Task ReadAnchored_WithoutTrailer_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        InitRepo();
        RunGit("commit", "--allow-empty", "-q", "-m", "no trailer");

        var act = () => SimingSessionCheckpointAnchoring.ReadAnchoredAsync(
            repoPath, "HEAD", ct);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*Siming-Checkpoint*");
    }

    [Fact]
    public async Task VerifyAnchored_WrongSession_FailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
        InitRepo();
        var anchor = await SimingSessionCheckpointAnchoring.AnchorAsync(
            store, sessionId, repoPath, "checkpoint", ct);
        var other = GuyabanoSessionId.New();
        await store.AppendAsync(new SessionEventRequest(
            other, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);

        var result = await SimingSessionCheckpointAnchoring.VerifyAnchoredAsync(
            store, anchor with { SessionId = other }, repoPath, ct);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task CrashSimulation_AbandonedStore_ReopensVerifiesRebuildsAndContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = GuyabanoSessionId.New();
        var projectionPath = Path.Combine(rootPath, "projections.db");
        var crashed = new SimingSessionEventStore(
            rootPath,
            projectionStore: new SqliteSessionProjectionStore(projectionPath, pooling: false));
        for (var index = 0; index < 3; index++)
            await crashed.AppendAsync(new SessionEventRequest(
                sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow,
                PayloadJson: $"{{\"n\":{index}}}"), ct);
        // Simulate a process kill: abandon the store without disposing it.
        crashed = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();

        await using var recovered = new SimingSessionEventStore(
            rootPath,
            projectionStore: new SqliteSessionProjectionStore(projectionPath, pooling: false));
        var fourth = await recovered.AppendAsync(new SessionEventRequest(
            sessionId, "guyabano", SessionEventTypes.WorkflowStarted, DateTimeOffset.UtcNow), ct);
        fourth.Sequence.Should().Be(4);

        var events = await recovered.ReadAsync(sessionId, cancellationToken: ct);
        events.Select(item => item.Sequence).Should().Equal(1, 2, 3, 4);
        (await recovered.VerifyChainAsync(sessionId, ct)).Should().BeEquivalentTo(fourth);

        var rebuilt = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "rebuilt.db"), pooling: false);
        var snapshot = await rebuilt.RebuildAsync(sessionId, events, ct);
        snapshot.Should().NotBeNull();
    }

    private void InitRepo()
    {
        Directory.CreateDirectory(repoPath);
        RunGit("init", "-q");
        RunGit("config", "user.email", "tests@example.com");
        RunGit("config", "user.name", "tests");
        RunGit("commit", "--allow-empty", "-q", "-m", "init");
    }

    private void RunGit(params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        process.WaitForExit();
        process.ExitCode.Should().Be(0);
    }

    public void Dispose()
    {
        // Git marks object files read-only; normalize before cleanup.
        foreach (var root in new[] { rootPath, repoPath })
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }
}
