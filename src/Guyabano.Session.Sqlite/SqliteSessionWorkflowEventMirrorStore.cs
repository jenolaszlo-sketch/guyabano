using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Guyabano.Session.Sqlite;

/// <summary>SQLite cursor store for at-least-once Zhinu-to-Siming delivery.</summary>
public sealed class SqliteSessionWorkflowEventMirrorStore :
    ISessionWorkflowEventMirrorStore
{
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly bool pooling;

    public SqliteSessionWorkflowEventMirrorStore(
        string databasePath,
        TimeProvider? timeProvider = null,
        bool pooling = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pooling = pooling;
    }

    public async Task<SessionWorkflowEventMirrorCursor?> GetAsync(
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("A workflow run ID is required.", nameof(workflowRunId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, workflow_run_id, mirrored_sequence, updated_at
            FROM session_workflow_event_mirrors
            WHERE session_id = $sessionId AND workflow_run_id = $runId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$runId", workflowRunId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Map(reader)
            : null;
    }

    public async Task<SessionWorkflowEventMirrorCursor> AdvanceAsync(
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        long expectedSequence,
        long mirroredSequence,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("A workflow run ID is required.", nameof(workflowRunId));
        if (expectedSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSequence));
        if (mirroredSequence != expectedSequence + 1)
            throw new ArgumentOutOfRangeException(
                nameof(mirroredSequence),
                "The mirror cursor must advance by exactly one event.");
        var updatedAt = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = expectedSequence == 0
            ? """
                INSERT INTO session_workflow_event_mirrors(
                    session_id, workflow_run_id, mirrored_sequence, updated_at)
                VALUES($sessionId, $runId, $next, $updatedAt)
                ON CONFLICT(session_id, workflow_run_id) DO NOTHING;
                """
            : """
                UPDATE session_workflow_event_mirrors
                SET mirrored_sequence = $next, updated_at = $updatedAt
                WHERE session_id = $sessionId AND workflow_run_id = $runId
                  AND mirrored_sequence = $expected;
                """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$runId", workflowRunId.ToString("D"));
        command.Parameters.AddWithValue("$expected", expectedSequence);
        command.Parameters.AddWithValue("$next", mirroredSequence);
        command.Parameters.AddWithValue(
            "$updatedAt",
            updatedAt.ToString("O", CultureInfo.InvariantCulture));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadAsync(
            connection, transaction, sessionId, workflowRunId, cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
        if (changed == 1 || current?.MirroredSequence == mirroredSequence)
            return current ?? new SessionWorkflowEventMirrorCursor(
                sessionId, workflowRunId, mirroredSequence, updatedAt);
        throw new InvalidOperationException(
            $"Zhinu mirror cursor conflict for workflow '{workflowRunId:D}': " +
            $"expected {expectedSequence}, found {current?.MirroredSequence.ToString(CultureInfo.InvariantCulture) ?? "missing"}.");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = pooling,
            DefaultTimeout = 30
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS session_workflow_event_mirrors(
                session_id TEXT NOT NULL,
                workflow_run_id TEXT NOT NULL,
                mirrored_sequence INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(session_id, workflow_run_id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<SessionWorkflowEventMirrorCursor?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session_id, workflow_run_id, mirrored_sequence, updated_at
            FROM session_workflow_event_mirrors
            WHERE session_id = $sessionId AND workflow_run_id = $runId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$runId", workflowRunId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Map(reader)
            : null;
    }

    private static SessionWorkflowEventMirrorCursor Map(SqliteDataReader reader) =>
        new(
            GuyabanoSessionId.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetInt64(2),
            DateTimeOffset.Parse(
                reader.GetString(3),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
}
