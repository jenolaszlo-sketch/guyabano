using Microsoft.Extensions.Options;
using Guyabano.Session;

namespace Guyabano.WorkflowWorker;

public sealed class CodeGenerationWorkspaceResolver(
    IOptions<CodeGenerationWorkerOptions> options,
    IGuyabanoSessionStore sessionStore)
{
    public string OutputRoot => Path.GetFullPath(options.Value.OutputRoot);

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

    /// <summary>
    /// Ensures that an uninitialized session has a stable workspace before any
    /// filesystem-backed provider is asked to inspect it. A missing workspace
    /// with an accepted revision is never recreated as an empty directory,
    /// because that would conceal loss of authoritative session state.
    /// </summary>
    public CodeGenerationWorkspace EnsureAvailable(GuyabanoSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var workspace = Resolve(session.Id);
        if (Directory.Exists(workspace.HostPath))
            return workspace;
        if (session.CurrentWorkspaceRevision is not null)
        {
            throw new SessionWorkspaceUnavailableException(
                session.Id,
                workspace.HostPath,
                session.CurrentWorkspaceRevision);
        }

        Directory.CreateDirectory(workspace.HostPath);
        return workspace;
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

    public CodeGenerationStaging ResolveStaging(
        GuyabanoSessionId sessionId,
        string mutationId)
    {
        ValidateMutationId(mutationId);
        var sessionSegment = sessionId.ToString();
        var settings = options.Value;
        var hostPath = Path.GetFullPath(Path.Combine(
            settings.OutputRoot,
            "sessions",
            sessionSegment,
            "staging",
            mutationId,
            "workspace"));
        var ciPath = CombineRelative(
            settings.CiRelativePath,
            "sessions",
            sessionSegment,
            "staging",
            mutationId,
            "workspace");
        return new CodeGenerationStaging(sessionId, mutationId, hostPath, ciPath);
    }

    public string ResolveStagingRoot(
        GuyabanoSessionId sessionId,
        string mutationId)
    {
        ValidateMutationId(mutationId);
        return Path.GetFullPath(Path.Combine(
            options.Value.OutputRoot,
            "sessions",
            sessionId.ToString(),
            "staging",
            mutationId));
    }

    private static void ValidateMutationId(string mutationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationId);
        if (mutationId is "." or ".." ||
            Path.IsPathRooted(mutationId) ||
            mutationId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            mutationId.Contains(Path.DirectorySeparatorChar) ||
            mutationId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "The mutation ID must be one safe path segment.",
                nameof(mutationId));
        }
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

public sealed record CodeGenerationStaging(
    GuyabanoSessionId SessionId,
    string MutationId,
    string HostPath,
    string CiRelativePath);

public sealed record CodeGenerationWorkspace(
    GuyabanoSessionId SessionId,
    string HostPath,
    string CiRelativePath);

public sealed class SessionWorkspaceUnavailableException : IOException
{
    public SessionWorkspaceUnavailableException(
        GuyabanoSessionId sessionId,
        string workspacePath,
        string acceptedRevision)
        : base(
            $"Session '{sessionId}' workspace '{workspacePath}' is missing, " +
            $"but accepted revision '{acceptedRevision}' is recorded. The " +
            "workspace was not recreated; restore or reconcile the accepted " +
            "revision before continuing.")
    {
        SessionId = sessionId;
        WorkspacePath = workspacePath;
        AcceptedRevision = acceptedRevision;
    }

    public GuyabanoSessionId SessionId { get; }

    public string WorkspacePath { get; }

    public string AcceptedRevision { get; }
}
