using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Guyabano.Session.Sqlite;

/// <summary>Rebuildable current-state projections for independently stored session ledgers.</summary>
public sealed class SqliteSessionProjectionStore : ISessionProjectionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string databasePath;

    public SqliteSessionProjectionStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
    }

    public async Task ApplyAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, sessionEvent.SessionId, cancellationToken).ConfigureAwait(false);
        if (current is not null && sessionEvent.Sequence <= current.AppliedSequence)
        {
            if (sessionEvent.Sequence == current.AppliedSequence &&
                !string.Equals(sessionEvent.Hash, current.HeadHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Session projection head conflict for '{sessionEvent.SessionId}' at sequence {sessionEvent.Sequence}.");
            return;
        }
        var expected = (current?.AppliedSequence ?? 0) + 1;
        if (sessionEvent.Sequence != expected)
            throw new InvalidOperationException($"Session projection gap for '{sessionEvent.SessionId}': expected sequence {expected}, received {sessionEvent.Sequence}.");
        var state = SessionTimelineProjection.Apply(current?.State, sessionEvent);
        await WriteAsync(connection, transaction, new SessionProjectionSnapshot(
                sessionEvent.SessionId, sessionEvent.Sequence, sessionEvent.Hash, state), cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<SessionProjectionSnapshot?> GetAsync(GuyabanoSessionId sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, null, sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionProjectionSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, applied_sequence, head_hash, state_json FROM session_projections ORDER BY session_id;";
        var result = new List<SessionProjectionSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Map(reader));
        return result;
    }

    public async Task<SessionProjectionSnapshot?> RebuildAsync(
        GuyabanoSessionId sessionId,
        IReadOnlyList<SessionEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Any(item => item.SessionId != sessionId))
            throw new ArgumentException("Every rebuilt event must belong to the requested session.", nameof(events));
        var ordered = events.OrderBy(item => item.Sequence).ToArray();
        for (var index = 0; index < ordered.Length; index++)
            if (ordered[index].Sequence != index + 1)
                throw new InvalidOperationException($"Cannot rebuild session '{sessionId}': sequence {index + 1} is missing.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM session_projections WHERE session_id = $sessionId;";
            delete.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        SessionCurrentState? state = null;
        foreach (var sessionEvent in ordered) state = SessionTimelineProjection.Apply(state, sessionEvent);
        if (state is null) { transaction.Commit(); return null; }
        var snapshot = new SessionProjectionSnapshot(sessionId, ordered[^1].Sequence, ordered[^1].Hash, state);
        await WriteAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return snapshot;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS session_projections(
                session_id TEXT PRIMARY KEY NOT NULL,
                applied_sequence INTEGER NOT NULL,
                head_hash TEXT NOT NULL,
                state_json TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<SessionProjectionSnapshot?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id, applied_sequence, head_hash, state_json FROM session_projections WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static async Task WriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionProjectionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_projections(session_id, applied_sequence, head_hash, state_json)
            VALUES($sessionId, $sequence, $headHash, $state)
            ON CONFLICT(session_id) DO UPDATE SET
                applied_sequence = excluded.applied_sequence,
                head_hash = excluded.head_hash,
                state_json = excluded.state_json;
            """;
        command.Parameters.AddWithValue("$sessionId", snapshot.SessionId.ToString());
        command.Parameters.AddWithValue("$sequence", snapshot.AppliedSequence);
        command.Parameters.AddWithValue("$headHash", snapshot.HeadHash);
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(snapshot.State, SerializerOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SessionProjectionSnapshot Map(SqliteDataReader reader) =>
        new(
            GuyabanoSessionId.Parse(reader.GetString(0)),
            reader.GetInt64(1),
            reader.GetString(2),
            JsonSerializer.Deserialize<SessionCurrentState>(reader.GetString(3), SerializerOptions)
                ?? throw new InvalidDataException("Session projection state is empty."));
}
