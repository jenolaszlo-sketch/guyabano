using System.Text.Json;

namespace Guyabano.Session;

public sealed class FileSystemGuyabanoSessionStore :
    IGuyabanoSessionStore,
    IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string rootPath;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileSystemGuyabanoSessionStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(this.rootPath);
    }

    public async Task<GuyabanoSession> CreateAsync(
        string repositoryId,
        string workspaceId,
        GuyabanoSessionId? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var id = sessionId ?? GuyabanoSessionId.New();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(SessionPath(id)))
                throw new InvalidOperationException(
                    $"Session '{id}' already exists.");

            var session = new GuyabanoSession
            {
                Id = id,
                RepositoryId = repositoryId,
                WorkspaceId = workspaceId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await WriteAsync(session, cancellationToken).ConfigureAwait(false);
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GuyabanoSession?> GetAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(SessionPath(sessionId), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GuyabanoSession?> FindByWorkflowRunAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         rootPath,
                         "session.json",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await ReadAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (session?.WorkflowRunIds.Contains(workflowRunId) == true)
                    return session;
            }

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GuyabanoSession> AttachWorkflowRunAsync(
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await ReadAsync(
                    SessionPath(sessionId),
                    cancellationToken)
                .ConfigureAwait(false) ??
                throw new KeyNotFoundException(
                    $"Session '{sessionId}' does not exist.");
            if (session.WorkflowRunIds.Contains(workflowRunId))
                return session;

            session = session with
            {
                WorkflowRunIds = session.WorkflowRunIds
                    .Append(workflowRunId)
                    .ToArray()
            };
            await WriteAsync(session, cancellationToken).ConfigureAwait(false);
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GuyabanoSession?> UpdateWorkspaceRevisionAsync(
        GuyabanoSessionId sessionId,
        string? expectedRevision,
        string replacementRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementRevision);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await ReadAsync(
                    SessionPath(sessionId),
                    cancellationToken)
                .ConfigureAwait(false) ??
                throw new KeyNotFoundException(
                    $"Session '{sessionId}' does not exist.");
            if (!string.Equals(
                    session.CurrentWorkspaceRevision,
                    expectedRevision,
                    StringComparison.Ordinal))
            {
                return null;
            }

            session = session with
            {
                CurrentWorkspaceRevision = replacementRevision
            };
            await WriteAsync(session, cancellationToken).ConfigureAwait(false);
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    private string SessionPath(GuyabanoSessionId sessionId) =>
        Path.Combine(rootPath, sessionId.ToString(), "session.json");

    private static async Task<GuyabanoSession?> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync<GuyabanoSession>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteAsync(
        GuyabanoSession session,
        CancellationToken cancellationToken)
    {
        var path = SessionPath(session.Id);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"session.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        session,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
