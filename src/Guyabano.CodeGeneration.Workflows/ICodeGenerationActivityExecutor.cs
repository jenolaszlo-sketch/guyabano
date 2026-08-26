using Penghou.Zhinu;

namespace Guyabano.CodeGeneration.Workflows;

public interface ICodeGenerationActivityExecutor
{
    Task<TOutput> ExecuteAsync<TOutput>(
        WorkflowContext workflow,
        string stepKey,
        string activityName,
        object input,
        StepOptions options,
        CancellationToken cancellationToken = default);
}
