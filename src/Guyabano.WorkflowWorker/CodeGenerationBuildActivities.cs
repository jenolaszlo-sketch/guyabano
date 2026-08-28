using System.Text.Json;
using Microsoft.Extensions.Options;
using Guyabano.Artifacts;
using Guyabano.CI.Client;
using Guyabano.CI.Contracts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Messaging;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationBuildActivities(
    IGuyabanoCiClient ciClient,
    IArtifactRepository artifactRepository,
    CangjieRevisionedConceptService cangjieConcepts,
    IWorkflowProgressPublisher progressPublisher,
    CodeGenerationWorkspaceResolver workspaceResolver,
    IOptions<CodeGenerationWorkerOptions> options,
    ILogger<CodeGenerationBuildActivities> logger)
{
    public async Task<CodeGenerationBuildResult> BuildAsync(
        CodeGenerationBuildRequest request)
    {
        var context = CodeGenerationActivityExecutionContext.Current;
        var info = context.Info;
        var workflowId = info.WorkflowId ??
            throw new InvalidOperationException(
                "Workflow activity information did not include a workflow ID.");
        var workspace = await workspaceResolver.ResolveWorkflowAsync(
            workflowId,
            context.CancellationToken);
        var paths = ToRelativePaths(
            workspace.HostPath,
            request.WrittenFiles);

        await PublishSafelyAsync(workflowId, new WorkflowProgress(
            WorkflowProgressEventType.Started,
            "Building",
            $"Building {request.ProjectOrSolutionFile}.",
            DateTimeOffset.UtcNow,
            RunId: info.WorkflowRunId,
            ActivityId: info.ActivityId,
            Attempt: request.BuildAttempt,
            MaximumAttempts: request.MaximumBuildAttempts,
            FileChecks:
                CodeGenerationCompilationFileChecks.CreateRunning(paths)));

        try
        {
            var diagnostics = new List<CiDiagnostic>();
            CiStreamEvent? resultEvent = null;
            string? serviceError = null;

            await foreach (var streamEvent in ciClient.BuildAsync(
                new CiBuildRequest(
                    workspace.CiRelativePath,
                    request.ProjectOrSolutionFile),
                context.CancellationToken))
            {
                if (streamEvent.Diagnostic is not null)
                {
                    diagnostics.Add(streamEvent.Diagnostic);
                }

                if (streamEvent.Type == "error")
                {
                    serviceError = streamEvent.Message;
                }

                if (streamEvent.Type == "result")
                {
                    resultEvent = streamEvent;
                }
            }

            diagnostics = diagnostics
                .DistinctBy(diagnostic => new
                {
                    diagnostic.Code,
                    diagnostic.Severity,
                    diagnostic.Message,
                    diagnostic.FilePath,
                    diagnostic.Line,
                    diagnostic.Column
                })
                .ToList();
            var succeeded = resultEvent?.Success == true;
            var errorCount = diagnostics.Count(diagnostic =>
                diagnostic.Severity == CiDiagnosticSeverity.Error);
            var error = succeeded
                ? null
                : serviceError ?? (errorCount > 0
                    ? $"Build failed with {errorCount} compiler error(s)."
                    : $"dotnet build exited with code {resultEvent?.ExitCode?.ToString() ?? "unknown"}.");
            var fileChecks =
                CodeGenerationCompilationFileChecks.CreateCompleted(
                    paths,
                    succeeded,
                    diagnostics);
            var buildResult = new CodeGenerationBuildResult(
                succeeded,
                resultEvent?.ExitCode,
                error,
                diagnostics.Select(MapDiagnostic).ToArray());

            // Publish validation evidence as authoritative artifact before durable progress
            try
            {
                var zhinuContext = CodeGenerationZhinuStepScope.Current;
                var stepKey = zhinuContext?.StepKey ?? info.ActivityId;
                var stepRevision = zhinuContext?.Revision ?? request.BuildAttempt;
                var evidence = new ValidationEvidencePayload(
                    BuildResult: buildResult,
                    SessionId: workspace.SessionId.ToString(),
                    WorkflowRunId: workflowId,
                    StepKey: stepKey,
                    StepRevision: stepRevision,
                    WorkspaceHostPath: workspace.HostPath,
                    WorkspaceCiPath: workspace.CiRelativePath,
                    EvaluatedFiles: paths,
                    PublishedAt: DateTimeOffset.UtcNow,
                    WorkspaceRevisionId: request.WorkspaceRevisionId);
                await artifactRepository.WriteAsync(
                    new ArtifactWriteRequest<ValidationEvidencePayload>(
                        WorkflowId: workflowId,
                        Kind: "validation-evidence",
                        SchemaVersion: 1,
                        StageKey: $"build-{request.BuildAttempt}",
                        Status: ArtifactStatus.Validated,
                        Payload: evidence),
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to publish validation evidence for workflow {WorkflowId}.", workflowId);
            }

            try
            {
                var cangjieContext = CodeGenerationZhinuStepScope.Current;
                var cangjieStepKey = cangjieContext?.StepKey ?? info.ActivityId;
                var cangjieStepRevision = cangjieContext?.Revision ?? request.BuildAttempt;
                var repositoryId = options.Value.RepositoryContextEnabled ? options.Value.RepositoryId : null;
                var evidenceKey = $"build:{workflowId}:{request.BuildAttempt}";
                var evidenceContent = JsonSerializer.Serialize(buildResult, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await cangjieConcepts.StoreEvidenceAsync(
                    sessionId: workspace.SessionId.ToString(),
                    evidenceKey: evidenceKey,
                    content: evidenceContent,
                    workflowRunId: workflowId,
                    stepKey: cangjieStepKey,
                    stepRevision: cangjieStepRevision,
                    repositoryId: repositoryId,
                    cancellationToken: context.CancellationToken);
                if (!buildResult.Succeeded && !string.IsNullOrWhiteSpace(buildResult.Error))
                {
                    var knowledgeKey = $"build-failure:{buildResult.Error!.GetHashCode():X}";
                    var knowledgeContent = $"Build failed: {buildResult.Error} with {diagnostics.Count} diagnostics. Lesson: inspect diagnostics and repair.";
                    await cangjieConcepts.StoreKnowledgeAsync(
                        sessionId: workspace.SessionId.ToString(),
                        knowledgeKey: knowledgeKey,
                        content: knowledgeContent,
                        workflowRunId: workflowId,
                        stepKey: cangjieStepKey,
                        stepRevision: cangjieStepRevision,
                        repositoryId: repositoryId,
                        cancellationToken: context.CancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to store Cangjie evidence for workflow {WorkflowId}", workflowId);
            }

            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                succeeded
                    ? WorkflowProgressEventType.Completed
                    : WorkflowProgressEventType.Failed,
                succeeded ? "Build completed" : "Build failed",
                succeeded
                    ? "The generated solution compiled successfully."
                    : error!,
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: request.BuildAttempt,
                Succeeded: succeeded,
                Diagnostics: diagnostics
                    .Select(CodeGenerationCompilationFileChecks.MapDiagnostic)
                    .ToArray(),
                MaximumAttempts: request.MaximumBuildAttempts,
                WillRetry: !succeeded &&
                    request.BuildAttempt < request.MaximumBuildAttempts,
                FileChecks: fileChecks));

            return buildResult;
        }
        catch (OperationCanceledException)
        {
            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                WorkflowProgressEventType.Canceled,
                "Build canceled",
                "The generated solution build was canceled.",
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: request.BuildAttempt,
                Succeeded: false,
                MaximumAttempts: request.MaximumBuildAttempts));
            throw;
        }
        catch (Exception exception)
        {
            await PublishSafelyAsync(workflowId, new WorkflowProgress(
                WorkflowProgressEventType.Failed,
                "Build failed",
                exception.Message,
                DateTimeOffset.UtcNow,
                RunId: info.WorkflowRunId,
                ActivityId: info.ActivityId,
                Attempt: request.BuildAttempt,
                Succeeded: false,
                MaximumAttempts: request.MaximumBuildAttempts));

            throw new CodeGenerationActivityException(
                exception.Message,
                exception,
                errorType: exception.GetType().Name,
                nonRetryable: true);
        }
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
                "Unable to publish {EventType} build progress for workflow {WorkflowId}.",
                progress.EventType,
                workflowId);
        }
    }

    private static IReadOnlyList<string> ToRelativePaths(
        string outputRoot,
        IReadOnlyList<string> writtenFiles)
    {
        var fullRoot = Path.GetFullPath(outputRoot);
        return writtenFiles
            .Select(path => Path.IsPathRooted(path)
                ? Path.GetRelativePath(fullRoot, Path.GetFullPath(path))
                : path)
            .Where(path => path != ".." &&
                !Normalize(path).StartsWith("../", StringComparison.Ordinal))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/');

    private static CodeGenerationBuildDiagnostic MapDiagnostic(
        CiDiagnostic diagnostic) =>
        new(
            diagnostic.Tool,
            diagnostic.Code,
            diagnostic.Severity.ToString(),
            diagnostic.Message,
            diagnostic.FilePath,
            diagnostic.ProjectPath,
            diagnostic.Line,
            diagnostic.Column);
}
