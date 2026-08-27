namespace Guyabano.Artifacts;

public sealed record ArtifactEnvelope<TPayload>(
    ArtifactReference Reference,
    string WorkflowId,
    string StageKey,
    DateTimeOffset CreatedAt,
    ArtifactStatus Status,
    IReadOnlyList<ArtifactReference> Inputs,
    TPayload Payload)
{
    public string? SessionId { get; init; }
}
