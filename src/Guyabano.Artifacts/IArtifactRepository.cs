namespace Guyabano.Artifacts;

public interface IArtifactRepository
{
    Task<ArtifactEnvelope<TPayload>> WriteAsync<TPayload>(
        ArtifactWriteRequest<TPayload> request,
        CancellationToken cancellationToken = default);

    Task<ArtifactEnvelope<TPayload>?> ReadAsync<TPayload>(
        ArtifactReference reference,
        CancellationToken cancellationToken = default);

    Task<ArtifactEnvelope<TPayload>?> ReadLatestAsync<TPayload>(
        string workflowId,
        string kind,
        string stageKey,
        CancellationToken cancellationToken = default);
}
