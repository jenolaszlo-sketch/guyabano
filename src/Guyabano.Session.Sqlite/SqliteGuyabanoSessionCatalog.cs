using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Guyabano.Session.Sqlite;

/// <summary>
/// Concurrency-safe operational catalog for session identity, workflow routing,
/// and accepted workspace revisions. Immutable conversation evidence remains in
/// the per-session Siming ledger; this database is authoritative mutable state.
/// </summary>
public sealed class SqliteGuyabanoSessionCatalog :
    IGuyabanoSessionStore,
    ISessionDecisionLeaseProvider,
    ISessionLifecycleReceiptStore,
    ISessionWorkspacePromotionCommitStore
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly bool pooling;

    public SqliteGuyabanoSessionCatalog(
        string databasePath,
        TimeProvider? timeProvider = null,
        bool pooling = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pooling = pooling;
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
        var createdAt = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions(session_id, repository_id, workspace_id, created_at, current_workspace_revision, version)
            VALUES($sessionId, $repositoryId, $workspaceId, $createdAt, NULL, 0);
            """;
        command.Parameters.AddWithValue("$sessionId", id.ToString());
        command.Parameters.AddWithValue("$repositoryId", repositoryId);
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"Session '{id}' already exists.", exception);
        }
        await InsertLifecycleReceiptAsync(
            connection,
            transaction,
            id,
            SessionEventTypes.SessionCreated,
            createdAt,
            $"session:{id}:created",
            null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sessionId"] = id.ToString(),
                ["repositoryId"] = repositoryId,
                ["workspaceId"] = workspaceId,
                ["catalogVersion"] = "0"
            },
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();

        return new GuyabanoSession
        {
            Id = id,
            RepositoryId = repositoryId,
            WorkspaceId = workspaceId,
            CreatedAt = createdAt
        };
    }

    public async Task<GuyabanoSession?> GetAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSessionAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GuyabanoSession>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, repository_id, workspace_id, created_at, current_workspace_revision, version
            FROM sessions
            ORDER BY created_at DESC, session_id DESC;
            """;
        var headers = new List<(GuyabanoSessionId Id, string RepositoryId, string WorkspaceId, DateTimeOffset CreatedAt, string? Revision, long Version)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                headers.Add(MapHeader(reader));
        }

        var sessions = new List<GuyabanoSession>(headers.Count);
        foreach (var header in headers)
            sessions.Add(await ReadSessionAsync(connection, header.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session '{header.Id}' disappeared while listing the catalog."));
        return sessions;
    }

    public async Task<GuyabanoSession?> FindByWorkflowRunAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            return null;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM session_workflow_runs WHERE workflow_run_id = $runId;";
        command.Parameters.AddWithValue("$runId", workflowRunId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string raw
            ? await ReadSessionAsync(connection, GuyabanoSessionId.Parse(raw), cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<GuyabanoSession> AttachWorkflowRunAsync(
        GuyabanoSessionId sessionId,
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("A non-empty workflow run ID is required.", nameof(workflowRunId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await ReadSessionAsync(connection, sessionId, cancellationToken, transaction).ConfigureAwait(false) is null)
            throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_workflow_runs(workflow_run_id, session_id, attached_at)
            VALUES($runId, $sessionId, $attachedAt)
            ON CONFLICT(workflow_run_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$runId", workflowRunId.ToString("D"));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$attachedAt", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        var attached = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var owner = connection.CreateCommand();
        owner.Transaction = transaction;
        owner.CommandText = "SELECT session_id FROM session_workflow_runs WHERE workflow_run_id = $runId;";
        owner.Parameters.AddWithValue("$runId", workflowRunId.ToString("D"));
        var actualOwner = (string?)await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualOwner, sessionId.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Workflow run '{workflowRunId:D}' already belongs to session '{actualOwner}'.");
        if (attached == 1)
        {
            await using var advance = connection.CreateCommand();
            advance.Transaction = transaction;
            advance.CommandText = "UPDATE sessions SET version = version + 1 WHERE session_id = $sessionId;";
            advance.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await InsertLifecycleReceiptAsync(
                connection,
                transaction,
                sessionId,
                SessionEventTypes.WorkflowAttached,
                timeProvider.GetUtcNow(),
                $"session:{sessionId}:workflow:{workflowRunId:D}:attached",
                workflowRunId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId.ToString(),
                    ["workflowRunId"] = workflowRunId.ToString("D")
                },
                cancellationToken).ConfigureAwait(false);
        }
        transaction.Commit();
        return await ReadSessionAsync(connection, sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared after workflow attachment.");
    }

    public async Task<GuyabanoSession?> UpdateWorkspaceRevisionAsync(
        GuyabanoSessionId sessionId,
        string? expectedRevision,
        string replacementRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementRevision);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = expectedRevision is null
            ? "UPDATE sessions SET current_workspace_revision = $replacement, version = version + 1 WHERE session_id = $sessionId AND current_workspace_revision IS NULL;"
            : "UPDATE sessions SET current_workspace_revision = $replacement, version = version + 1 WHERE session_id = $sessionId AND current_workspace_revision = $expected;";
        command.Parameters.AddWithValue("$replacement", replacementRevision);
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        if (expectedRevision is not null)
            command.Parameters.AddWithValue("$expected", expectedRevision);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed == 1)
        {
            await InsertLifecycleReceiptAsync(
                connection,
                transaction,
                sessionId,
                SessionEventTypes.WorkspaceRevisionAccepted,
                timeProvider.GetUtcNow(),
                $"session:{sessionId}:workspace:{replacementRevision}",
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId.ToString(),
                    ["fromRevision"] = expectedRevision ?? "uninitialized",
                    ["toRevision"] = replacementRevision
                },
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return await ReadSessionAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
        }
        if (await ReadSessionAsync(connection, sessionId, cancellationToken, transaction).ConfigureAwait(false) is null)
            throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
        transaction.Commit();
        return null;
    }

    public async Task<GuyabanoSession?> CommitWorkspacePromotionAsync(
        GuyabanoSessionId sessionId,
        string expectedRevision,
        string replacementRevision,
        string mutationId,
        Guid? workflowRunId,
        DateTimeOffset promotedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sessions
            SET current_workspace_revision = $replacement, version = version + 1
            WHERE session_id = $sessionId AND current_workspace_revision = $expected;
            """;
        command.Parameters.AddWithValue("$replacement", replacementRevision);
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$expected", expectedRevision);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed == 1)
        {
            await InsertLifecycleReceiptAsync(
                connection,
                transaction,
                sessionId,
                SessionEventTypes.WorkspacePromoted,
                promotedAt,
                $"session:{sessionId}:promotion:{mutationId}:{replacementRevision}",
                workflowRunId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId.ToString(),
                    ["mutationId"] = mutationId,
                    ["fromRevision"] = expectedRevision,
                    ["toRevision"] = replacementRevision,
                    ["workflowRunId"] = workflowRunId?.ToString("D") ?? "(none)",
                    ["auditSource"] = "transactional-workspace-promotion"
                },
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return await ReadSessionAsync(connection, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        if (await ReadSessionAsync(
                connection, sessionId, cancellationToken, transaction)
            .ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
        }
        transaction.Commit();
        return null;
    }

    public async ValueTask<ISessionDecisionLease> AcquireAsync(
        GuyabanoSessionId sessionId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A non-empty operation ID is required.", nameof(operationId));
        var token = Guid.CreateVersion7();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = timeProvider.GetUtcNow();
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var previousLease = await ReadDecisionLeaseAsync(
                connection, transaction, sessionId, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO session_decision_leases(session_id, operation_id, fencing_token, acquired_at, expires_at)
                VALUES($sessionId, $operationId, $token, $now, $expires)
                ON CONFLICT(session_id) DO UPDATE SET
                    operation_id = excluded.operation_id,
                    fencing_token = excluded.fencing_token,
                    acquired_at = excluded.acquired_at,
                    expires_at = excluded.expires_at
                WHERE session_decision_leases.expires_at <= $now;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            command.Parameters.AddWithValue("$token", token.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$expires", (now + LeaseDuration).ToString("O", CultureInfo.InvariantCulture));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                if (previousLease is not null && previousLease.Value.ExpiresAt <= now)
                {
                    await InsertLifecycleReceiptAsync(
                        connection,
                        transaction,
                        sessionId,
                        SessionEventTypes.DecisionLeaseExpired,
                        now,
                        $"session:{sessionId}:decision-lease:{previousLease.Value.Token:D}:expired",
                        previousLease.Value.OperationId,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["sessionId"] = sessionId.ToString(),
                            ["operationId"] = previousLease.Value.OperationId.ToString("D"),
                            ["fencingToken"] = previousLease.Value.Token.ToString("D"),
                            ["expiredAt"] = previousLease.Value.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                await InsertLifecycleReceiptAsync(
                    connection,
                    transaction,
                    sessionId,
                    SessionEventTypes.DecisionLeaseAcquired,
                    now,
                    $"session:{sessionId}:decision-lease:{token:D}:acquired",
                    operationId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["sessionId"] = sessionId.ToString(),
                        ["operationId"] = operationId.ToString("D"),
                        ["fencingToken"] = token.ToString("D"),
                        ["expiresAt"] = (now + LeaseDuration).ToString("O", CultureInfo.InvariantCulture)
                    },
                    cancellationToken).ConfigureAwait(false);
                transaction.Commit();
                return new SqliteDecisionLease(this, sessionId, operationId, token, now);
            }
            transaction.Commit();
            await Task.Delay(RetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RenewAsync(GuyabanoSessionId sessionId, Guid token, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE session_decision_leases SET expires_at = $expires WHERE session_id = $sessionId AND fencing_token = $token;";
        command.Parameters.AddWithValue("$expires", (now + LeaseDuration).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$token", token.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"Decision lease for session '{sessionId}' was lost.");
    }

    private async Task ReleaseAsync(
        GuyabanoSessionId sessionId,
        Guid operationId,
        Guid token)
    {
        await using var connection = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM session_decision_leases WHERE session_id = $sessionId AND fencing_token = $token;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$token", token.ToString("D"));
        if (await command.ExecuteNonQueryAsync().ConfigureAwait(false) == 1)
        {
            var now = timeProvider.GetUtcNow();
            await InsertLifecycleReceiptAsync(
                connection,
                transaction,
                sessionId,
                SessionEventTypes.DecisionLeaseReleased,
                now,
                $"session:{sessionId}:decision-lease:{token:D}:released",
                operationId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId.ToString(),
                    ["operationId"] = operationId.ToString("D"),
                    ["fencingToken"] = token.ToString("D")
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    public async Task<IReadOnlyList<SessionLifecycleReceipt>> ListPendingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT receipt_id, session_id, event_type, occurred_at, idempotency_key,
                   correlation_id, cross_system_refs_json, delivered_at
            FROM session_lifecycle_receipts
            WHERE delivered_at IS NULL
            ORDER BY occurred_at, receipt_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maximumCount);
        var result = new List<SessionLifecycleReceipt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new SessionLifecycleReceipt
            {
                ReceiptId = Guid.Parse(reader.GetString(0)),
                SessionId = GuyabanoSessionId.Parse(reader.GetString(1)),
                EventType = reader.GetString(2),
                OccurredAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                IdempotencyKey = reader.GetString(4),
                CorrelationId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                CrossSystemRefs = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6))
                    ?? new Dictionary<string, string>(StringComparer.Ordinal),
                DeliveredAt = reader.IsDBNull(7)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });
        return result;
    }

    public async Task MarkDeliveredAsync(
        Guid receiptId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        if (receiptId == Guid.Empty)
            throw new ArgumentException("A non-empty receipt ID is required.", nameof(receiptId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE session_lifecycle_receipts SET delivered_at = COALESCE(delivered_at, $deliveredAt) WHERE receipt_id = $receiptId;";
        command.Parameters.AddWithValue("$deliveredAt", deliveredAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$receiptId", receiptId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new KeyNotFoundException($"Lifecycle receipt '{receiptId:D}' does not exist.");
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
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS sessions(
                session_id TEXT PRIMARY KEY NOT NULL,
                repository_id TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                current_workspace_revision TEXT NULL,
                version INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS session_workflow_runs(
                workflow_run_id TEXT PRIMARY KEY NOT NULL,
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                attached_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_session_workflow_runs_session
                ON session_workflow_runs(session_id, attached_at);
            CREATE TABLE IF NOT EXISTS session_decision_leases(
                session_id TEXT PRIMARY KEY NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                operation_id TEXT NOT NULL,
                fencing_token TEXT NOT NULL,
                acquired_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS session_lifecycle_receipts(
                receipt_id TEXT PRIMARY KEY NOT NULL,
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                event_type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                correlation_id TEXT NULL,
                cross_system_refs_json TEXT NOT NULL,
                delivered_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_session_lifecycle_pending
                ON session_lifecycle_receipts(delivered_at, occurred_at, receipt_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<GuyabanoSession?> ReadSessionAsync(
        SqliteConnection connection,
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id, repository_id, workspace_id, created_at, current_workspace_revision, version FROM sessions WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var header = MapHeader(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        var workflowRunIds = await ReadWorkflowRunIdsAsync(
            connection, header.Id, cancellationToken, transaction).ConfigureAwait(false);
        return new GuyabanoSession
        {
            Id = header.Id,
            RepositoryId = header.RepositoryId,
            WorkspaceId = header.WorkspaceId,
            CreatedAt = header.CreatedAt,
            CurrentWorkspaceRevision = header.Revision,
            WorkflowRunIds = workflowRunIds,
            Version = header.Version
        };
    }

    private static (GuyabanoSessionId Id, string RepositoryId, string WorkspaceId, DateTimeOffset CreatedAt, string? Revision, long Version) MapHeader(
        SqliteDataReader reader) =>
        (
            GuyabanoSessionId.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5)
        );

    private static async Task<IReadOnlyList<Guid>> ReadWorkflowRunIdsAsync(
        SqliteConnection connection,
        GuyabanoSessionId id,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var runs = connection.CreateCommand();
        runs.Transaction = transaction;
        runs.CommandText = "SELECT workflow_run_id FROM session_workflow_runs WHERE session_id = $sessionId ORDER BY attached_at, workflow_run_id;";
        runs.Parameters.AddWithValue("$sessionId", id.ToString());
        var workflowRunIds = new List<Guid>();
        await using var runReader = await runs.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await runReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            workflowRunIds.Add(Guid.Parse(runReader.GetString(0)));
        return workflowRunIds;
    }

    private static async Task InsertLifecycleReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GuyabanoSessionId sessionId,
        string eventType,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        Guid? correlationId,
        IReadOnlyDictionary<string, string> crossSystemRefs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_lifecycle_receipts(
                receipt_id, session_id, event_type, occurred_at, idempotency_key,
                correlation_id, cross_system_refs_json, delivered_at)
            VALUES($receiptId, $sessionId, $eventType, $occurredAt, $key,
                $correlationId, $refs, NULL)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$receiptId", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$correlationId", correlationId is null
            ? DBNull.Value
            : correlationId.Value.ToString("D"));
        command.Parameters.AddWithValue("$refs", JsonSerializer.Serialize(crossSystemRefs));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(Guid OperationId, Guid Token, DateTimeOffset ExpiresAt)?> ReadDecisionLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT operation_id, fencing_token, expires_at FROM session_decision_leases WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return (
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private sealed class SqliteDecisionLease : ISessionDecisionLease
    {
        private readonly SqliteGuyabanoSessionCatalog owner;
        private readonly Guid token;
        private readonly CancellationTokenSource stop = new();
        private readonly Task renewal;
        private int disposed;

        public SqliteDecisionLease(
            SqliteGuyabanoSessionCatalog owner,
            GuyabanoSessionId sessionId,
            Guid operationId,
            Guid token,
            DateTimeOffset acquiredAt)
        {
            this.owner = owner;
            this.token = token;
            SessionId = sessionId;
            OperationId = operationId;
            AcquiredAt = acquiredAt;
            renewal = RenewLoopAsync();
        }

        public GuyabanoSessionId SessionId { get; }
        public Guid OperationId { get; }
        public DateTimeOffset AcquiredAt { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            await stop.CancelAsync().ConfigureAwait(false);
            Exception? renewalFailure = null;
            try { await renewal.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception exception) { renewalFailure = exception; }
            try
            {
                await owner.ReleaseAsync(SessionId, OperationId, token).ConfigureAwait(false);
            }
            finally
            {
                stop.Dispose();
            }
            if (renewalFailure is not null)
                throw new InvalidOperationException(
                    $"Decision lease for session '{SessionId}' could not be renewed reliably.",
                    renewalFailure);
        }

        private async Task RenewLoopAsync()
        {
            while (true)
            {
                await Task.Delay(RenewalInterval, owner.timeProvider, stop.Token).ConfigureAwait(false);
                await owner.RenewAsync(SessionId, token, stop.Token).ConfigureAwait(false);
            }
        }
    }
}
