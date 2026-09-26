using System.Diagnostics;
using Penghou.Siming;

namespace Guyabano.Session.Sqlite;

/// <summary>
/// Anchors selected Siming session-ledger checkpoints to Git commits as
/// <c>Siming-Checkpoint</c> trailers, and re-verifies ledgers against them.
/// The trailer carries the base64 portable checkpoint document; verification
/// binds the ledger to the anchored head.
/// </summary>
public static class SimingSessionCheckpointAnchoring
{
    /// <summary>Gets the Git trailer name carrying anchored checkpoints.</summary>
    public const string TrailerName = "Siming-Checkpoint";

    /// <summary>Describes one anchored session checkpoint.</summary>
    /// <param name="SessionId">Anchored session.</param>
    /// <param name="CommitSha">Commit carrying the trailer.</param>
    /// <param name="AnchoredAt">Anchor creation time.</param>
    public sealed record Anchor(
        GuyabanoSessionId SessionId, string CommitSha, DateTimeOffset AnchoredAt);

    /// <summary>Captures the session head and anchors it to a new commit.</summary>
    public static async Task<Anchor> AnchorAsync(
        SimingSessionEventStore store,
        GuyabanoSessionId sessionId,
        string repositoryPath,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var checkpoint = await store.CaptureCheckpointAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        var trailer = $"{TrailerName}: {Convert.ToBase64String(LedgerCheckpoints.Export(checkpoint))}";
        await RunGitAsync(repositoryPath, cancellationToken, "commit", "--allow-empty", "-q",
                "-m", message, "-m", trailer)
            .ConfigureAwait(false);
        var sha = (await ReadGitAsync(repositoryPath, cancellationToken, "rev-parse", "HEAD")
            .ConfigureAwait(false)).Trim();
        return new Anchor(sessionId, sha, DateTimeOffset.UtcNow);
    }

    /// <summary>Reads the checkpoint anchored to a commit back into a document.</summary>
    public static async Task<LedgerCheckpoint> ReadAnchoredAsync(
        string repositoryPath, string commitSha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        var body = await ReadGitAsync(
                repositoryPath, cancellationToken, "show", "-s", "--format=%B", commitSha)
            .ConfigureAwait(false);
        var trailer = body.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line =>
                line.StartsWith(TrailerName + ":", StringComparison.Ordinal));
        if (trailer is null)
            throw new InvalidDataException(
                $"Commit '{commitSha}' carries no '{TrailerName}' trailer.");
        return LedgerCheckpoints.Import(Convert.FromBase64String(
            trailer[(TrailerName.Length + 1)..].Trim()));
    }

    /// <summary>Verifies the session ledger against its anchored checkpoint.</summary>
    public static async Task<LedgerVerificationResult> VerifyAnchoredAsync(
        SimingSessionEventStore store,
        Anchor anchor,
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(anchor);
        var checkpoint = await ReadAnchoredAsync(
                repositoryPath, anchor.CommitSha, cancellationToken)
            .ConfigureAwait(false);
        return await store.VerifyAsync(anchor.SessionId, checkpoint, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RunGitAsync(
        string repositoryPath, CancellationToken cancellationToken, params string[] args)
    {
        using var process = StartGit(repositoryPath, args);
        process.Start();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed: {await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false)}");
    }

    private static async Task<string> ReadGitAsync(
        string repositoryPath, CancellationToken cancellationToken, params string[] args)
    {
        using var process = StartGit(repositoryPath, args);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed: {await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false)}");
        return output;
    }

    private static Process StartGit(string repositoryPath, string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        return new Process { StartInfo = startInfo };
    }
}
