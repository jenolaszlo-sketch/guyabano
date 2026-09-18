namespace Guyabano.Artifacts;

/// <summary>
/// Revisioned planning artifact catalog over an <see cref="IArtifactRepository"/>.
/// Payloads are stored as content-addressed artifacts; identity, revision,
/// provenance, and lifecycle state live in a per-workflow catalog index.
/// </summary>
/// <remarks>
/// The catalog assumes a single writer per workflow, matching how a planning
/// workflow executes its nodes. Mutating operations are serialized within one
/// catalog instance.
/// </remarks>
public sealed class PlanningArtifactCatalog : IPlanningArtifactCatalog
{
    /// <summary>The artifact kind used for the catalog index.</summary>
    public const string IndexKind = "planning-catalog";

    /// <summary>The stage key used for the catalog index.</summary>
    public const string IndexStageKey = "index";

    private const int IndexSchemaVersion = 1;

    private readonly IArtifactRepository contentStore;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset lastIndexCreatedAt = DateTimeOffset.MinValue;

    public PlanningArtifactCatalog(IArtifactRepository contentStore)
    {
        this.contentStore = contentStore ??
            throw new ArgumentNullException(nameof(contentStore));
    }

    /// <inheritdoc />
    public async Task<PlanningArtifactRecord> PublishAsync<TPayload>(
        PublishPlanningArtifactRequest<TPayload> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProducedBy);
        if (request.SchemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(request));

        var inputs = request.Inputs?.ToArray() ?? [];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await ReadIndexAsync(request.WorkflowId, cancellationToken)
                .ConfigureAwait(false) ?? PlanningArtifactIndex.Empty;
            var inputRecords = inputs
                .Select(input => Find(index, input) ??
                    throw new PlanningArtifactNotFoundException(request.WorkflowId, input))
                .ToArray();
            var revision = index.Records
                .Where(record => record.Key == request.Key)
                .Select(record => record.Revision)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var content = await contentStore.WriteAsync(
                new ArtifactWriteRequest<TPayload>(
                    request.WorkflowId,
                    request.Key.Kind,
                    request.SchemaVersion,
                    request.Key.Name,
                    ArtifactStatus.Validated,
                    request.Payload,
                    inputRecords.Select(record => record.Content).ToArray())
                {
                    SessionId = request.SessionId,
                },
                cancellationToken).ConfigureAwait(false);
            var record = new PlanningArtifactRecord(
                new PlanningArtifactVersion(request.Key, revision),
                content.Reference,
                request.WorkflowId,
                request.ProducedBy,
                inputs,
                request.State,
                content.CreatedAt)
            {
                SessionId = request.SessionId,
            };
            var records = index.Records
                .Select(existing => existing.Key == request.Key &&
                                    existing.State != PlanningArtifactState.Superseded
                    ? existing with { State = PlanningArtifactState.Superseded }
                    : existing)
                .Append(record)
                .ToArray();
            await WriteIndexAsync(
                request.WorkflowId,
                new PlanningArtifactIndex(records),
                request.SessionId,
                cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PlanningArtifactRecord?> GetAsync(
        string workflowId,
        PlanningArtifactVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(version);
        var index = await ReadIndexAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        return index is null ? null : Find(index, version);
    }

    /// <inheritdoc />
    public async Task<PlanningArtifactRecord?> GetCurrentAsync(
        string workflowId,
        PlanningArtifactKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(key);
        var index = await ReadIndexAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        return index?.Records
            .Where(record => record.Key == key)
            .OrderByDescending(record => record.Revision)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlanningArtifactRecord>> ListRevisionsAsync(
        string workflowId,
        PlanningArtifactKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(key);
        var index = await ReadIndexAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        return index is null
            ? []
            : index.Records
                .Where(record => record.Key == key)
                .OrderBy(record => record.Revision)
                .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlanningArtifactRecord>> ListCurrentAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        var index = await ReadIndexAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        return index is null
            ? []
            : index.Records
                .GroupBy(record => record.Key)
                .Select(group => group
                    .OrderByDescending(record => record.Revision)
                    .First())
                .OrderBy(record => record.Key.Kind, StringComparer.Ordinal)
                .ThenBy(record => record.Key.Name, StringComparer.Ordinal)
                .ToArray();
    }

    /// <inheritdoc />
    public async Task<TPayload> ReadPayloadAsync<TPayload>(
        PlanningArtifactRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var envelope = await contentStore.ReadAsync<TPayload>(
            record.Content,
            cancellationToken).ConfigureAwait(false) ??
            throw new ArtifactIntegrityException(
                $"Planning artifact '{record.Version.Value}' could not be read from the content store.");
        return envelope.Payload;
    }

    /// <inheritdoc />
    public async Task<PlanningArtifactImpact> InvalidateAsync(
        string workflowId,
        PlanningArtifactVersion changed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(changed);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await ReadIndexAsync(workflowId, cancellationToken)
                .ConfigureAwait(false) ?? PlanningArtifactIndex.Empty;
            if (Find(index, changed) is null)
                throw new PlanningArtifactNotFoundException(workflowId, changed);

            // The changed revision stays valid. Seeding with the other
            // revisions of the same identity means a downstream revision that
            // pinned an earlier revision (now superseded) is still recognised
            // as affected.
            var stale = index.Records
                .Where(record => record.Key == changed.Key &&
                                 record.Version != changed)
                .Select(record => record.Version)
                .ToHashSet();
            var progressed = true;
            while (progressed)
            {
                progressed = false;
                foreach (var record in index.Records)
                {
                    if (!stale.Contains(record.Version) &&
                        record.Inputs.Any(stale.Contains))
                    {
                        stale.Add(record.Version);
                        progressed = true;
                    }
                }
            }

            var stateChanged = false;
            var records = index.Records
                .Select(record =>
                {
                    if (stale.Contains(record.Version) &&
                        record.State != PlanningArtifactState.Superseded &&
                        record.State != PlanningArtifactState.Stale)
                    {
                        stateChanged = true;
                        return record with { State = PlanningArtifactState.Stale };
                    }

                    return record;
                })
                .ToArray();
            if (stateChanged)
            {
                await WriteIndexAsync(
                    workflowId,
                    new PlanningArtifactIndex(records),
                    sessionId: null,
                    cancellationToken).ConfigureAwait(false);
            }

            var affected = records
                .Where(record => stale.Contains(record.Version))
                .OrderBy(record => record.CreatedAt)
                .ThenBy(record => record.Version.Value, StringComparer.Ordinal)
                .ToArray();
            return new PlanningArtifactImpact(changed, affected);
        }
        finally
        {
            gate.Release();
        }
    }

    private static PlanningArtifactRecord? Find(
        PlanningArtifactIndex index,
        PlanningArtifactVersion version) =>
        index.Records.FirstOrDefault(record => record.Version == version);

    private async Task<PlanningArtifactIndex?> ReadIndexAsync(
        string workflowId,
        CancellationToken cancellationToken)
    {
        var envelope = await contentStore.ReadLatestAsync<PlanningArtifactIndex>(
            workflowId,
            IndexKind,
            IndexStageKey,
            cancellationToken).ConfigureAwait(false);
        return envelope?.Payload;
    }

    private async Task WriteIndexAsync(
        string workflowId,
        PlanningArtifactIndex index,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        // The content store resolves the latest index by timestamp, so
        // successive index writes within one catalog must observe strictly
        // increasing creation times. Mutating operations hold the gate, which
        // makes this check-and-write sequence safe for the single writer.
        while (DateTimeOffset.UtcNow <= lastIndexCreatedAt)
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        var envelope = await contentStore.WriteAsync(
            new ArtifactWriteRequest<PlanningArtifactIndex>(
                workflowId,
                IndexKind,
                IndexSchemaVersion,
                IndexStageKey,
                ArtifactStatus.Validated,
                index)
            {
                SessionId = sessionId,
            },
            cancellationToken).ConfigureAwait(false);
        lastIndexCreatedAt = envelope.CreatedAt;
    }
}
