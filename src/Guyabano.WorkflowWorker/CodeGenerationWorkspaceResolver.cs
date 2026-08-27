using Microsoft.Extensions.Options;
using Guyabano.Session;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationWorkspaceResolver(
    IOptions<CodeGenerationWorkerOptions> options,
    IGuyabanoSessionStore sessionStore)
{
    public CodeGenerationWorkspace Resolve(GuyabanoSessionId sessionId)
    {
        var sessionSegment = sessionId.ToString();
        var settings = options.Value;
        var hostPath = Path.GetFullPath(Path.Combine(
            settings.OutputRoot,
            "sessions",
            sessionSegment,
            "workspace"));
        var ciPath = CombineRelative(
            settings.CiRelativePath,
            "sessions",
            sessionSegment,
            "workspace");
        return new CodeGenerationWorkspace(sessionId, hostPath, ciPath);
    }

    public async Task<CodeGenerationWorkspace> ResolveWorkflowAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        if (!Guid.TryParse(workflowId, out var runId))
            throw new ArgumentException(
                "The workflow ID must be a GUID.",
                nameof(workflowId));
        var session = await sessionStore.FindByWorkflowRunAsync(
                runId,
                cancellationToken)
            .ConfigureAwait(false) ??
            throw new InvalidOperationException(
                $"Workflow '{workflowId}' is not associated with a Guyabano session.");
        return Resolve(session.Id);
    }

    private static string CombineRelative(params string[] segments) =>
        string.Join(
            '/',
            segments
                .SelectMany(segment => segment
                    .Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries))
                .Where(segment => segment != "."));
}

public sealed record CodeGenerationWorkspace(
    GuyabanoSessionId SessionId,
    string HostPath,
    string CiRelativePath);
