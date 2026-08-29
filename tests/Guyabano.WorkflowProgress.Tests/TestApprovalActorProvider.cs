using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

internal sealed class TestApprovalActorProvider : IApprovalActorProvider
{
    public ApprovalActor GetRequiredActor() =>
        new("tester", "Test User", "test-authentication");
}
