using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

internal sealed class TestApprovalActorProvider : IAuthenticatedActorProvider
{
    public AuthenticatedActor GetRequiredActor() =>
        new("tester", "Test User", "test-authentication");
}
