using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowWorker;

internal interface IRepositoryContextService
{
    Task<RepositoryRevision> IndexAsync(
        RepositoryIndexRequest request,
        CancellationToken cancellationToken);

    Task<RepositoryContextSelection> SelectAsync(
        RepositoryContextSelectionRequest request,
        CancellationToken cancellationToken);

    Task<RepositoryContextReference> CaptureAsync(
        RepositoryContextCaptureRequest request,
        CancellationToken cancellationToken);
}
