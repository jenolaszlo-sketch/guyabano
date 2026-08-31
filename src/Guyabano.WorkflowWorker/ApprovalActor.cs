namespace Guyabano.WorkflowWorker;

public sealed record AuthenticatedActor(
    string SubjectId,
    string DisplayName,
    string AuthenticationType);

/// <summary>
/// Resolves an actor from trusted host authentication state. Privileged
/// commands deliberately contain no free-form actor field.
/// </summary>
public interface IAuthenticatedActorProvider
{
    AuthenticatedActor GetRequiredActor();
}

public sealed class RejectingAuthenticatedActorProvider : IAuthenticatedActorProvider
{
    public AuthenticatedActor GetRequiredActor() => throw new UnauthorizedAccessException(
        "This operation requires an authenticated host actor.");
}
