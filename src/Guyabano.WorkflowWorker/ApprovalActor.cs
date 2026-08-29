namespace Guyabano.WorkflowWorker;

public sealed record ApprovalActor(
    string SubjectId,
    string DisplayName,
    string AuthenticationType);

/// <summary>
/// Resolves an approval actor from trusted host authentication state. Approval
/// commands deliberately contain no free-form actor field.
/// </summary>
public interface IApprovalActorProvider
{
    ApprovalActor GetRequiredActor();
}

public sealed class RejectingApprovalActorProvider : IApprovalActorProvider
{
    public ApprovalActor GetRequiredActor() => throw new UnauthorizedAccessException(
        "Restart approval requires an authenticated host actor.");
}
