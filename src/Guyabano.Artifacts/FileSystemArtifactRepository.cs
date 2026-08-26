using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guyabano.Artifacts;

public sealed class FileSystemArtifactRepository : IArtifactRepository
{
    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();
    private readonly string _rootPath;

    public FileSystemArtifactRepository(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public async Task<ArtifactEnvelope<TPayload>> WriteAsync<TPayload>(
        ArtifactWriteRequest<TPayload> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StageKey);
        if (request.SchemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(request.SchemaVersion));

        var inputs = request.Inputs?.ToArray() ?? [];
        var contentHash = ComputeContentHash(
            request.Kind,
            request.SchemaVersion,
            request.Status,
            inputs,
            request.Payload);
        var artifactId = $"{request.Kind}:{contentHash}";
        var relativePath = Path.Combine(
            "runs",
            ToPathSegment(request.WorkflowId),
            ToPathSegment(request.Kind),
            ToPathSegment(request.StageKey),
            $"{contentHash}.json");
        var reference = new ArtifactReference(
            artifactId,
            request.Kind,
            request.SchemaVersion,
            relativePath.Replace('\\', '/'),
            contentHash);
        var envelope = new ArtifactEnvelope<TPayload>(
            reference,
            request.WorkflowId,
            request.StageKey,
            DateTimeOffset.UtcNow,
            request.Status,
            inputs,
            request.Payload);
        var finalPath = ResolvePath(reference.RelativePath);
        var directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);

        if (File.Exists(finalPath))
        {
            return await ReadRequiredAsync<TPayload>(
                reference,
                cancellationToken);
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{contentHash}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    envelope,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            try
            {
                File.Move(temporaryPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                File.Delete(temporaryPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return await ReadRequiredAsync<TPayload>(
            reference,
            cancellationToken);
    }

    public async Task<ArtifactEnvelope<TPayload>?> ReadAsync<TPayload>(
        ArtifactReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var path = ResolvePath(reference.RelativePath);
        if (!File.Exists(path))
            return null;

        var envelope = await ReadStoredEnvelopeAsync<TPayload>(
            path,
            reference.ArtifactId,
            cancellationToken);
        ValidateEnvelope(reference, envelope);
        return envelope;
    }

    private static async Task<ArtifactEnvelope<TPayload>>
        ReadStoredEnvelopeAsync<TPayload>(
            string path,
            string artifactId,
            CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            var envelope = await JsonSerializer.DeserializeAsync<
                ArtifactEnvelope<TPayload>>(
                stream,
                SerializerOptions,
                cancellationToken);
            if (envelope is null)
                throw new ArtifactIntegrityException(
                    $"Artifact '{artifactId}' deserialized to null.");

            return envelope;
        }
        catch (JsonException exception)
        {
            throw new ArtifactIntegrityException(
                $"Artifact '{artifactId}' contains invalid JSON.",
                exception);
        }
    }

    public async Task<ArtifactEnvelope<TPayload>?> ReadLatestAsync<TPayload>(
        string workflowId,
        string kind,
        string stageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageKey);

        var directory = ResolvePath(Path.Combine(
            "runs",
            ToPathSegment(workflowId),
            ToPathSegment(kind),
            ToPathSegment(stageKey)));
        if (!Directory.Exists(directory))
            return null;

        ArtifactEnvelope<TPayload>? latest = null;
        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(_rootPath, path)
                .Replace('\\', '/');
            var candidate = await ReadStoredEnvelopeAsync<TPayload>(
                path,
                relativePath,
                cancellationToken);
            if (!candidate.WorkflowId.Equals(
                    workflowId,
                    StringComparison.Ordinal) ||
                !candidate.Reference.Kind.Equals(
                    kind,
                    StringComparison.Ordinal) ||
                !candidate.StageKey.Equals(
                    stageKey,
                    StringComparison.Ordinal) ||
                !candidate.Reference.RelativePath.Equals(
                    relativePath,
                    StringComparison.Ordinal))
            {
                throw new ArtifactIntegrityException(
                    $"Artifact '{candidate.Reference.ArtifactId}' is stored outside its declared workflow, kind, or stage path.");
            }

            ValidateEnvelope(candidate.Reference, candidate);
            if (latest is null || candidate.CreatedAt > latest.CreatedAt)
                latest = candidate;
        }

        return latest;
    }

    private async Task<ArtifactEnvelope<TPayload>> ReadRequiredAsync<TPayload>(
        ArtifactReference reference,
        CancellationToken cancellationToken) =>
        await ReadAsync<TPayload>(reference, cancellationToken) ??
        throw new ArtifactIntegrityException(
            $"Artifact '{reference.ArtifactId}' disappeared after it was written.");

    private static void ValidateEnvelope<TPayload>(
        ArtifactReference requested,
        ArtifactEnvelope<TPayload> envelope)
    {
        if (envelope.Reference != requested)
            throw new ArtifactIntegrityException(
                $"Artifact reference '{requested.ArtifactId}' does not match its stored envelope.");

        var actualHash = ComputeContentHash(
            envelope.Reference.Kind,
            envelope.Reference.SchemaVersion,
            envelope.Status,
            envelope.Inputs,
            envelope.Payload);
        if (!actualHash.Equals(
                requested.ContentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArtifactIntegrityException(
                $"Artifact '{requested.ArtifactId}' failed its content hash check.");
        }
    }

    private static string ComputeContentHash<TPayload>(
        string kind,
        int schemaVersion,
        ArtifactStatus status,
        IReadOnlyList<ArtifactReference> inputs,
        TPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ArtifactHashContent<TPayload>(
                kind,
                schemaVersion,
                status,
                inputs.Select(item => item.ArtifactId)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray(),
                payload),
            SerializerOptions);
        return Convert.ToHexString(
                SHA256.HashData(bytes))
            .ToLowerInvariant();
    }

    private string ResolvePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new ArtifactIntegrityException(
                "Artifact paths must be relative to the repository root.");

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(
            Path.Combine(_rootPath, normalized));
        var rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArtifactIntegrityException(
                "Artifact path escapes the repository root.");
        }

        return fullPath;
    }

    private static string ToPathSegment(string value)
    {
        if (value is not "." and not ".." &&
            value.All(character =>
                char.IsLetterOrDigit(character) ||
                character is '-' or '_' or '.'))
        {
            return value;
        }

        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(value));
        return $"encoded-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record ArtifactHashContent<TPayload>(
        string Kind,
        int SchemaVersion,
        ArtifactStatus Status,
        IReadOnlyList<string> InputArtifactIds,
        TPayload Payload);
}
