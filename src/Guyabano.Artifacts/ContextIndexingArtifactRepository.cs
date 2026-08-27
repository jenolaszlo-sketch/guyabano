using Penghou.Cangjie;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guyabano.Artifacts;

/// <summary>
/// Adds searchable context and provenance relationships around an authoritative
/// typed artifact repository.
/// </summary>
public sealed class ContextIndexingArtifactRepository(
    IArtifactRepository inner,
    IContextStore contextStore,
    string scope = ContextIndexingArtifactRepository.DefaultScope)
    : IArtifactRepository
{
    public const string DefaultScope = "guyabano:code-generation-artifacts";

    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();

    public async Task<ArtifactEnvelope<TPayload>> WriteAsync<TPayload>(
        ArtifactWriteRequest<TPayload> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var previous = await inner.ReadLatestAsync<TPayload>(
            request.WorkflowId,
            request.Kind,
            request.StageKey,
            cancellationToken: cancellationToken);
        var envelope = await inner.WriteAsync(request, cancellationToken);
        var indexed = await IndexAsync(envelope, cancellationToken);

        foreach (var input in envelope.Inputs)
        {
            var inputItem = await EnsureReferenceAsync(
                input,
                cancellationToken);
            await AddRelationAsync(
                indexed.Id,
                inputItem.Id,
                ContextRelationKinds.DerivedFrom,
                cancellationToken);
        }

        if (previous is not null &&
            previous.Reference.ArtifactId != envelope.Reference.ArtifactId)
        {
            var previousItem = await IndexAsync(
                previous,
                cancellationToken);
            await AddRelationAsync(
                indexed.Id,
                previousItem.Id,
                ContextRelationKinds.Supersedes,
                cancellationToken);
        }

        return envelope;
    }

    public Task<ArtifactEnvelope<TPayload>?> ReadAsync<TPayload>(
        ArtifactReference reference,
        CancellationToken cancellationToken = default) =>
        inner.ReadAsync<TPayload>(reference, cancellationToken);

    public Task<ArtifactEnvelope<TPayload>?> ReadLatestAsync<TPayload>(
        string workflowId,
        string kind,
        string stageKey,
        CancellationToken cancellationToken = default) =>
        inner.ReadLatestAsync<TPayload>(
            workflowId,
            kind,
            stageKey,
            cancellationToken: cancellationToken);

    private async ValueTask<ContextItem> IndexAsync<TPayload>(
        ArtifactEnvelope<TPayload> envelope,
        CancellationToken cancellationToken)
    {
        var reference = envelope.Reference;
        return await contextStore.StoreAsync(
            new ContextItem
            {
                Id = CreateContextId(reference.ArtifactId),
                Scope = scope,
                Key = reference.ArtifactId,
                Kind = ContextKinds.Artifact,
                Content = JsonSerializer.Serialize(
                    envelope.Payload,
                    SerializerOptions),
                Provenance = CreateProvenance(
                    reference,
                    envelope.CreatedAt),
                CreatedAt = envelope.CreatedAt,
                Metadata = new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["workflowId"] = envelope.WorkflowId,
                    ["sessionId"] = envelope.SessionId ?? string.Empty,
                    ["stageKey"] = envelope.StageKey,
                    ["artifactKind"] = reference.Kind,
                    ["schemaVersion"] =
                        reference.SchemaVersion.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    ["status"] = envelope.Status.ToString(),
                    ["relativePath"] = reference.RelativePath,
                    ["contentHash"] = reference.ContentHash
                },
                Tags =
                [
                    "artifact",
                    $"workflow:{envelope.WorkflowId}",
                    $"session:{envelope.SessionId ?? "unknown"}",
                    $"stage:{envelope.StageKey}",
                    $"kind:{reference.Kind}",
                    $"status:{envelope.Status}"
                ]
            },
            new ContextWriteOptions
            {
                IdempotencyKey = CreateIdempotencyKey(reference)
            },
            cancellationToken: cancellationToken);
    }

    private async ValueTask<ContextItem> EnsureReferenceAsync(
        ArtifactReference reference,
        CancellationToken cancellationToken)
    {
        var id = CreateContextId(reference.ArtifactId);
        var existing = await contextStore.GetAsync(id, cancellationToken);
        if (existing is not null)
            return existing;

        return await contextStore.StoreAsync(
            new ContextItem
            {
                Id = id,
                Scope = scope,
                Key = reference.ArtifactId,
                Kind = ContextKinds.Artifact,
                Content = $"Artifact {reference.Kind} {reference.ArtifactId}",
                Provenance = CreateProvenance(reference),
                Metadata = new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["artifactKind"] = reference.Kind,
                    ["schemaVersion"] =
                        reference.SchemaVersion.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    ["relativePath"] = reference.RelativePath,
                    ["contentHash"] = reference.ContentHash,
                    ["indexedAsReference"] = bool.TrueString
                },
                Tags = ["artifact", "reference", $"kind:{reference.Kind}"]
            },
            new ContextWriteOptions
            {
                IdempotencyKey = CreateIdempotencyKey(reference)
            },
            cancellationToken: cancellationToken);
    }

    private async ValueTask AddRelationAsync(
        Guid fromId,
        Guid toId,
        string kind,
        CancellationToken cancellationToken)
    {
        if (fromId == toId)
            return;

        await contextStore.AddRelationAsync(
            new ContextRelation
            {
                FromId = fromId,
                ToId = toId,
                Kind = kind
            },
            cancellationToken);
    }

    private static ContextSource CreateSource(ArtifactReference reference) =>
        new()
        {
            Uri = $"guyabano-artifact:{reference.RelativePath}",
            Kind = reference.Kind,
            ContentHash = reference.ContentHash
        };

    private static ContextProvenance CreateProvenance(
        ArtifactReference reference,
        DateTimeOffset? originatedAt = null) =>
        new()
        {
            Source = CreateSource(reference),
            Producer = "guyabano:artifact-indexer",
            OriginatedAt = originatedAt
        };

    private static string CreateIdempotencyKey(
        ArtifactReference reference) =>
        $"guyabano-artifact:{reference.ArtifactId}";

    private static Guid CreateContextId(string artifactId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(artifactId));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
