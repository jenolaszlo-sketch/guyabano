using System.Text.Json;
using Microsoft.Extensions.Options;
using Penghou.Cangjie;
using Penghou.Zhinu;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Session;

namespace Guyabano.WorkflowWorker;

/// <summary>
/// Cross-store session consistency audit spanning Zhinu workflow artifacts, Cangjie
/// snapshots, Hetu publications, Baize execution evidence, and the filesystem
/// workspace. Detects missing references, stale revisions, mismatched validation
/// evidence, and incomplete cross-store publication.
/// </summary>
public sealed class SessionConsistencyAuditService(
    WorkflowEngine workflowEngine,
    IGuyabanoSessionStore sessionStore,
    ISessionEventStore sessionEvents,
    IContextStore contextStore,
    IArtifactRepository artifactRepository,
    CodeGenerationWorkspaceResolver workspaceResolver,
    IOptions<CodeGenerationWorkerOptions> options)
{
    public async Task<SessionAuditReport> AuditAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<SessionAuditFinding>();
        var session = await sessionStore.GetAsync(
                new GuyabanoSessionId(sessionId),
                cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return new SessionAuditReport(
                sessionId,
                DateTimeOffset.UtcNow,
                WorkflowRunsChecked: 0,
                ArtifactsResolved: 0,
                SnapshotsResolved: 0,
                [new SessionAuditFinding(SessionAuditSeverity.Error, "session", $"Session '{sessionId}' does not exist.")]);
        }

        // Session event chain integrity
        try
        {
            await sessionEvents.VerifyChainAsync(session.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            findings.Add(new SessionAuditFinding(
                SessionAuditSeverity.Error,
                "session-events",
                exception.Message));
        }

        // Workspace revision consistency
        var workspace = workspaceResolver.Resolve(session.Id);
        if (session.CurrentWorkspaceRevision is not null)
        {
            if (!Directory.Exists(workspace.HostPath))
            {
                findings.Add(new SessionAuditFinding(
                    SessionAuditSeverity.Error,
                    "workspace",
                    $"Session workspace '{workspace.HostPath}' is missing but a revision '{session.CurrentWorkspaceRevision}' is recorded."));
            }
            else
            {
                var actual = await ComputeWorkspaceRevisionAsync(
                    workspace.HostPath,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                        actual,
                        session.CurrentWorkspaceRevision,
                        StringComparison.Ordinal))
                {
                    findings.Add(new SessionAuditFinding(
                        SessionAuditSeverity.Warning,
                        "workspace",
                        $"Workspace content hash '{actual}' does not match the accepted session revision '{session.CurrentWorkspaceRevision}'."));
                }
            }
        }

        var artifactsResolved = 0;
        var snapshotsResolved = 0;
        var hasRepositoryPublication = false;
        var hasValidationEvidence = false;
        var hasBaizeEvidence = false;
        var snapshotIds = new HashSet<Guid>();

        foreach (var runId in session.WorkflowRunIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Penghou.Zhinu.WorkflowArtifactReference>? artifacts = null;
            try
            {
                artifacts = await workflowEngine.GetArtifactsAsync(runId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                findings.Add(new SessionAuditFinding(
                    SessionAuditSeverity.Error,
                    "zhinu",
                    $"Workflow '{runId:D}' could not enumerate artifacts: {exception.Message}"));
                continue;
            }

            foreach (var artifact in artifacts)
            {
                var resolved = await ResolveArtifactAsync(
                    artifact,
                    cancellationToken).ConfigureAwait(false);
                if (resolved)
                    artifactsResolved++;
                else
                {
                    findings.Add(new SessionAuditFinding(
                        SessionAuditSeverity.Error,
                        "zhinu",
                        $"Workflow artifact '{artifact.Name}' does not resolve to an authoritative file."));
                }

                if (artifact.Metadata is not null &&
                    artifact.Metadata.TryGetValue("cangjieSnapshotId", out var snapshotRaw) &&
                    Guid.TryParse(snapshotRaw, out var snapshotId))
                {
                    snapshotIds.Add(snapshotId);
                }

                if (artifact.ArtifactType == "repository-publication")
                    hasRepositoryPublication = true;
                if (artifact.ArtifactType == "validation-evidence")
                    hasValidationEvidence = true;
                if (artifact.ArtifactType == "baize-execution")
                    hasBaizeEvidence = true;
            }
        }

        // Cangjie snapshots referenced by artifacts must resolve to an ordered selection
        foreach (var snapshotId in snapshotIds)
        {
            var resolved = await contextStore.ResolveSnapshotAsync(snapshotId, cancellationToken)
                .ConfigureAwait(false);
            if (resolved is null)
            {
                findings.Add(new SessionAuditFinding(
                    SessionAuditSeverity.Error,
                    "cangjie",
                    $"Cangjie snapshot '{snapshotId:D}' referenced by a workflow artifact does not resolve."));
            }
            else
            {
                snapshotsResolved++;
            }
        }

        // Hetu publication must carry the current workspace revision
        if (hasRepositoryPublication && session.CurrentWorkspaceRevision is not null)
        {
            var latestPublication = await artifactRepository.ReadLatestAsync<RepositoryReindexPublicationPayload>(
                session.WorkflowRunIds.LastOrDefault().ToString("D"),
                "repository-publication",
                "post-generation",
                cancellationToken).ConfigureAwait(false);
            if (latestPublication is not null &&
                !string.Equals(
                    latestPublication.Payload.WorkspaceRevisionId,
                    session.CurrentWorkspaceRevision,
                    StringComparison.Ordinal))
            {
                findings.Add(new SessionAuditFinding(
                    SessionAuditSeverity.Warning,
                    "hetu",
                    $"Latest Hetu publication revision '{latestPublication.Payload.WorkspaceRevisionId}' does not match the accepted session workspace revision '{session.CurrentWorkspaceRevision}'."));
            }
        }

        if (!hasRepositoryPublication)
        {
            findings.Add(new SessionAuditFinding(
                SessionAuditSeverity.Warning,
                "hetu",
                "No repository-publication artifact found for any session workflow run."));
        }

        if (!hasValidationEvidence)
        {
            findings.Add(new SessionAuditFinding(
                SessionAuditSeverity.Warning,
                "evidence",
                "No validation-evidence artifact found for any session workflow run."));
        }

        if (!hasBaizeEvidence)
        {
            findings.Add(new SessionAuditFinding(
                SessionAuditSeverity.Warning,
                "baize",
                "No baize-execution provenance artifact found for any session workflow run."));
        }

        return new SessionAuditReport(
            SessionId: sessionId,
            AuditedAt: DateTimeOffset.UtcNow,
            WorkflowRunsChecked: session.WorkflowRunIds.Count,
            ArtifactsResolved: artifactsResolved,
            SnapshotsResolved: snapshotsResolved,
            Findings: findings);
    }

    private async Task<bool> ResolveArtifactAsync(
        Penghou.Zhinu.WorkflowArtifactReference artifact,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifact.Location) ||
            string.IsNullOrWhiteSpace(artifact.ContentHash))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            options.Value.OutputRoot,
            ".gen",
            artifact.Location));
        if (!File.Exists(fullPath))
            return false;

        try
        {
            var json = await File.ReadAllTextAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var storedHash = document.RootElement
                .GetProperty("reference")
                .GetProperty("contentHash")
                .GetString();
            return string.Equals(storedHash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ComputeWorkspaceRevisionAsync(
        string workspaceHostPath,
        CancellationToken cancellationToken)
    {
        var snapshot = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(
            workspaceHostPath,
            cancellationToken).ConfigureAwait(false);
        var ordered = snapshot
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value.Hash}");
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("|", ordered));
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }
}
