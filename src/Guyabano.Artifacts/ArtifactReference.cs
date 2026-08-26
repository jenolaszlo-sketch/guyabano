namespace Guyabano.Artifacts;

public sealed record ArtifactReference(
    string ArtifactId,
    string Kind,
    int SchemaVersion,
    string RelativePath,
    string ContentHash);
