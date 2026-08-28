using System.Text.Json;

namespace Guyabano.Session;

/// <summary>
/// Durable prototype store for saga operation state. Zhinu remains the saga
/// coordinator; this store holds independently inspectable participant receipts
/// used to make retries and reconciliation deterministic.
/// </summary>
public sealed class FileSystemCrossStoreOperationStore :
    ICrossStoreOperationStore,
    IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string rootPath;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileSystemCrossStoreOperationStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(this.rootPath);
    }

    public async Task<CrossStoreOperation> StartAsync(
        StartCrossStoreOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindByIdempotencyKeyAsync(
                request.SessionId,
                request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.WorkflowRunId != request.WorkflowRunId ||
                    !string.Equals(existing.Kind, request.Kind, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Operation idempotency key '{request.IdempotencyKey}' is already used by a different operation.");
                }

                return existing;
            }

            var operation = new CrossStoreOperation
            {
                Id = request.OperationId ?? CrossStoreOperationId.New(),
                SessionId = request.SessionId,
                WorkflowRunId = request.WorkflowRunId,
                Kind = request.Kind,
                IdempotencyKey = request.IdempotencyKey,
                State = CrossStoreOperationState.Prepared,
                CreatedAt = request.StartedAt,
                UpdatedAt = request.StartedAt,
                Version = 1,
                Transitions =
                [
                    new CrossStoreOperationTransition
                    {
                        Sequence = 1,
                        State = CrossStoreOperationState.Prepared,
                        OccurredAt = request.StartedAt
                    }
                ]
            };
            if (File.Exists(OperationPath(operation.SessionId, operation.Id)))
                throw new InvalidOperationException($"Operation '{operation.Id}' already exists.");

            await WriteAsync(operation, cancellationToken).ConfigureAwait(false);
            return operation;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CrossStoreOperation?> GetAsync(
        CrossStoreOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Directory.EnumerateFiles(
                rootPath, $"{operationId}.json", SearchOption.AllDirectories).ToArray();
            if (paths.Length > 1)
                throw new InvalidOperationException($"Operation '{operationId}' is not unique.");
            return paths.Length == 0
                ? null
                : await ReadAsync(paths[0], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CrossStoreOperation?> FindByWorkflowRunAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var matches = new List<CrossStoreOperation>();
            foreach (var path in Directory.EnumerateFiles(
                         rootPath, "*.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = await ReadAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (candidate?.WorkflowRunId != workflowRunId)
                    continue;
                matches.Add(candidate);
            }
            var active = matches.Where(item =>
                item.State != CrossStoreOperationState.Completed).ToArray();
            if (active.Length > 1)
                throw new InvalidOperationException(
                    $"Workflow run '{workflowRunId:D}' has more than one active operation.");
            return active.SingleOrDefault() ?? matches
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CrossStoreOperation>> ListAsync(
        GuyabanoSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = SessionOperationDirectory(sessionId);
            if (!Directory.Exists(directory))
                return [];
            var result = new List<CrossStoreOperation>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = await ReadAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (operation is not null)
                    result.Add(operation);
            }
            return result.OrderBy(item => item.CreatedAt).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CrossStoreOperation> RecordParticipantAsync(
        CrossStoreOperationId operationId,
        CrossStoreParticipantReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.Participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.IdempotencyKey);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operation = await RequireAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            if (operation.State == CrossStoreOperationState.Completed)
                throw new InvalidOperationException("A completed operation cannot accept participant receipts.");

            var existing = operation.Participants.SingleOrDefault(item =>
                item.Participant.Equals(receipt.Participant, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (EquivalentReceipt(existing, receipt))
                    return operation;
                throw new InvalidOperationException(
                    $"Participant '{receipt.Participant}' already has a different immutable receipt.");
            }

            var expectedKey = operation.ParticipantIdempotencyKey(receipt.Participant);
            if (!string.Equals(receipt.IdempotencyKey, expectedKey, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Participant '{receipt.Participant}' receipt has an invalid idempotency key.");

            operation = operation with
            {
                Participants = operation.Participants.Append(receipt).ToArray(),
                UpdatedAt = receipt.RecordedAt,
                Version = operation.Version + 1
            };
            await WriteAsync(operation, cancellationToken).ConfigureAwait(false);
            return operation;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CrossStoreOperation> TransitionAsync(
        CrossStoreOperationId operationId,
        CrossStoreOperationState targetState,
        DateTimeOffset occurredAt,
        string? reconciliationReason = null,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operation = await RequireAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            if (operation.State == targetState)
                return operation;
            if (!CanTransition(operation.State, targetState))
                throw new InvalidOperationException(
                    $"Operation cannot transition from {operation.State} to {targetState}.");
            if (targetState == CrossStoreOperationState.ReconciliationRequired &&
                string.IsNullOrWhiteSpace(reconciliationReason))
            {
                throw new ArgumentException(
                    "A reconciliation reason is required.", nameof(reconciliationReason));
            }
            if (targetState == CrossStoreOperationState.Completed &&
                operation.Participants.Any(item => item.State == CrossStoreParticipantState.Failed))
            {
                throw new InvalidOperationException(
                    "An operation with a failed participant cannot complete.");
            }

            operation = operation with
            {
                State = targetState,
                ReconciliationReason = targetState ==
                    CrossStoreOperationState.ReconciliationRequired
                        ? reconciliationReason
                        : null,
                UpdatedAt = occurredAt,
                Version = operation.Version + 1,
                Transitions = operation.Transitions.Append(
                    new CrossStoreOperationTransition
                    {
                        Sequence = operation.Transitions.Count + 1,
                        State = targetState,
                        OccurredAt = occurredAt,
                        Reason = targetState ==
                            CrossStoreOperationState.ReconciliationRequired
                                ? reconciliationReason
                                : null
                    }).ToArray()
            };
            await WriteAsync(operation, cancellationToken).ConfigureAwait(false);
            return operation;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    private static bool EquivalentReceipt(
        CrossStoreParticipantReceipt existing,
        CrossStoreParticipantReceipt replay) =>
        existing.Participant == replay.Participant &&
        existing.IdempotencyKey == replay.IdempotencyKey &&
        existing.State == replay.State &&
        existing.BeforeIdentity == replay.BeforeIdentity &&
        existing.AfterIdentity == replay.AfterIdentity &&
        existing.ResultHash == replay.ResultHash &&
        existing.RecoveryAction == replay.RecoveryAction;

    private static bool CanTransition(
        CrossStoreOperationState source,
        CrossStoreOperationState target) =>
        target == CrossStoreOperationState.ReconciliationRequired ||
        (source, target) is
            (CrossStoreOperationState.Prepared, CrossStoreOperationState.WorkspacePromoted) or
            (CrossStoreOperationState.Prepared, CrossStoreOperationState.Published) or
            (CrossStoreOperationState.WorkspacePromoted, CrossStoreOperationState.Published) or
            (CrossStoreOperationState.Published, CrossStoreOperationState.Completed) or
            (CrossStoreOperationState.ReconciliationRequired, CrossStoreOperationState.WorkspacePromoted) or
            (CrossStoreOperationState.ReconciliationRequired, CrossStoreOperationState.Published) or
            (CrossStoreOperationState.ReconciliationRequired, CrossStoreOperationState.Completed);

    private async Task<CrossStoreOperation> RequireAsync(
        CrossStoreOperationId operationId,
        CancellationToken cancellationToken)
    {
        var paths = Directory.EnumerateFiles(
            rootPath, $"{operationId}.json", SearchOption.AllDirectories).ToArray();
        if (paths.Length != 1)
            throw new KeyNotFoundException($"Operation '{operationId}' does not exist.");
        return (await ReadAsync(paths[0], cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<CrossStoreOperation?> FindByIdempotencyKeyAsync(
        GuyabanoSessionId sessionId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var directory = SessionOperationDirectory(sessionId);
        if (!Directory.Exists(directory))
            return null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var operation = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.Equals(operation?.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                return operation;
        }
        return null;
    }

    private string SessionOperationDirectory(GuyabanoSessionId sessionId) =>
        Path.Combine(rootPath, sessionId.ToString(), "operations");

    private string OperationPath(
        GuyabanoSessionId sessionId,
        CrossStoreOperationId operationId) =>
        Path.Combine(SessionOperationDirectory(sessionId), $"{operationId}.json");

    private static async Task<CrossStoreOperation?> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<CrossStoreOperation>(
            stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(
        CrossStoreOperation operation,
        CancellationToken cancellationToken)
    {
        var path = OperationPath(operation.SessionId, operation.Id);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"operation.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream, operation, SerializerOptions, cancellationToken)
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
