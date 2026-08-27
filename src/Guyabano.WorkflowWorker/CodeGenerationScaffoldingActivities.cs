using Microsoft.Extensions.Options;
using Guyabano.CI.Client;
using Guyabano.CI.Contracts;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Messaging;
using System.Text.Json;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationScaffoldingActivities(
    IGuyabanoCiClient ciClient,
    IWorkflowProgressPublisher progressPublisher,
    CodeGenerationWorkspaceResolver workspaceResolver,
    ILogger<CodeGenerationScaffoldingActivities> logger)
{
    public async Task<CodeGenerationScaffoldingResult> ScaffoldAsync(
        CodeGenerationScaffoldingRequest request)
    {
        var context = CodeGenerationActivityExecutionContext.Current;
        var info = context.Info;
        var workflowId = info.WorkflowId ??
            throw new InvalidOperationException(
                "Workflow activity information did not include a workflow ID.");
        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            workflowId,
            context.CancellationToken);
        var scaffoldingTask = request.Plan.Tasks.SingleOrDefault(task =>
            task.ExecutionKind == PlanTaskExecutionKind.Scaffolding);

        if (scaffoldingTask is null)
            return await FailAsync(
                workflowId,
                info,
                "The plan does not contain a scaffolding task.");

        await PublishSafelyAsync(workflowId, new WorkflowProgress(
            WorkflowProgressEventType.Started,
            "Scaffolding",
            $"Creating {request.Plan.Solution.Path} and {request.Plan.Projects.Count} project(s) with dotnet.",
            DateTimeOffset.UtcNow,
            RunId: info.WorkflowRunId,
            ActivityId: info.ActivityId,
            Attempt: info.Attempt,
            MaximumAttempts: 1));

        try
        {
            CiStreamEvent? resultEvent = null;
            CiScaffoldResult? ciResult = null;
            string? serviceError = null;

            await foreach (var streamEvent in ciClient.ScaffoldAsync(
                CreateRequest(request.Plan, workspace.CiRelativePath),
                context.CancellationToken))
            {
                if (streamEvent.Type == "error")
                    serviceError = streamEvent.Message;

                if (streamEvent.Type == "result")
                {
                    resultEvent = streamEvent;
                    ciResult = DeserializeResult(streamEvent.Data);
                }
            }

            var succeeded = resultEvent?.Success == true && ciResult is not null;
            var artifacts = ciResult?.Artifacts ?? [];
            var removedFiles = ciResult?.RemovedFiles ?? [];
            var error = succeeded
                ? null
                : serviceError ??
                  $"dotnet scaffolding exited with code {resultEvent?.ExitCode?.ToString() ?? "unknown"}.";

            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                succeeded
                    ? WorkflowProgressEventType.Completed
                    : WorkflowProgressEventType.Failed,
                succeeded ? "Scaffolding completed" : "Scaffolding failed",
                succeeded
                    ? $"Created the solution structure with {ciResult!.OperationCount} dotnet operation(s)."
                    : error!,
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: info.Attempt,
                Succeeded: succeeded,
                Metadata: new Dictionary<string, string>
                {
                    ["taskId"] = scaffoldingTask.Id,
                    ["artifactCount"] = artifacts.Count.ToString(),
                    ["removedBoilerplateCount"] =
                        removedFiles.Count.ToString(),
                    ["completedOperations"] =
                        (ciResult?.OperationCount ?? 0).ToString()
                },
                MaximumAttempts: 1,
                WillRetry: false));

            return new CodeGenerationScaffoldingResult(
                succeeded,
                error,
                artifacts,
                removedFiles,
                ciResult?.OperationCount ?? 0);
        }
        catch (OperationCanceledException)
        {
            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                WorkflowProgressEventType.Canceled,
                "Scaffolding canceled",
                "The dotnet scaffolding activity was canceled.",
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: info.Attempt,
                Succeeded: false,
                MaximumAttempts: 1));
            throw;
        }
        catch (Exception exception)
        {
            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                WorkflowProgressEventType.Failed,
                "Scaffolding failed",
                exception.Message,
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: info.Attempt,
                Succeeded: false,
                MaximumAttempts: 1));

            throw new CodeGenerationActivityException(
                exception.Message,
                exception,
                errorType: exception.GetType().Name,
                nonRetryable: true);
        }
    }

    internal static CiScaffoldRequest CreateRequest(
        CodeGenerationPlan plan,
        string relativePath) =>
        new(
            relativePath,
            new CiScaffoldSolution(
                plan.Solution.Name,
                plan.Solution.Path),
            plan.Projects.Select(project => new CiScaffoldProject(
                project.Name,
                project.Path,
                project.Kind,
                project.TargetFramework,
                project.ProjectDependencies,
                project.Packages.Select(package =>
                    new CiScaffoldPackage(
                        package.Name,
                        package.Version)).ToArray())).ToArray());

    private async Task<CodeGenerationScaffoldingResult> FailAsync(
        string workflowId,
        CodeGenerationActivityInfo info,
        string error)
    {
        await PublishSafelyAsync(workflowId, new WorkflowProgress(
            WorkflowProgressEventType.Failed,
            "Scaffolding failed",
            error,
            DateTimeOffset.UtcNow,
            RunId: info.WorkflowRunId,
            ActivityId: info.ActivityId,
            Attempt: info.Attempt,
            Succeeded: false,
            MaximumAttempts: 1));

        return new CodeGenerationScaffoldingResult(
            false,
            error,
            [],
            [],
            0);
    }

    private async Task PublishSafelyAsync(
        string workflowId,
        WorkflowProgress progress)
    {
        try
        {
            await progressPublisher.PublishAsync(
                workflowId,
                progress,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to publish scaffolding progress for workflow {WorkflowId}.",
                workflowId);
        }
    }

    private static CiScaffoldResult? DeserializeResult(object? data) =>
        data switch
        {
            JsonElement element => element.Deserialize<CiScaffoldResult>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CiScaffoldResult result => result,
            _ => null
        };
}
