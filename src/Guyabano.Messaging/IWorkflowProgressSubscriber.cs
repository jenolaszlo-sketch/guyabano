namespace Guyabano.Messaging;

public interface IWorkflowProgressSubscriber
{
    IAsyncEnumerable<WorkflowProgressEntry> SubscribeAsync(
        string workflowId,
        string? afterEntryId = null,
        CancellationToken cancellationToken = default);
}
