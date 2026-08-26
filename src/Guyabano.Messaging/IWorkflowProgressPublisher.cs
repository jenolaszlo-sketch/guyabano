namespace Guyabano.Messaging;

public interface IWorkflowProgressPublisher
{
    Task<WorkflowProgressEntry> PublishAsync(
        string workflowId,
        WorkflowProgress progress,
        CancellationToken cancellationToken = default);
}
