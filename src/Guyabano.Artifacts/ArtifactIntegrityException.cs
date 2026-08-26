namespace Guyabano.Artifacts;

public sealed class ArtifactIntegrityException(
    string message,
    Exception? innerException = null)
    : Exception(message, innerException);
