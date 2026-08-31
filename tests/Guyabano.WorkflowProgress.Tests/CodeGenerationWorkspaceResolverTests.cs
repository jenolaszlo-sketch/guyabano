using FluentAssertions;
using Guyabano.Session;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Options;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationWorkspaceResolverTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-workspace-resolver-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureAvailable_CreatesStableWorkspaceForUninitializedSession()
    {
        var session = Session(currentWorkspaceRevision: null);
        var resolver = Resolver();

        var workspace = resolver.EnsureAvailable(session);

        Directory.Exists(workspace.HostPath).Should().BeTrue();
        workspace.HostPath.Should().Be(Path.Combine(
            Path.GetFullPath(rootPath),
            "sessions",
            session.Id.ToString(),
            "workspace"));
        workspace.CiRelativePath.Should().Be(
            $"generated/sessions/{session.Id}/workspace");
    }

    [Fact]
    public void EnsureAvailable_DoesNotHideMissingAcceptedWorkspace()
    {
        var session = Session("sha256:accepted");
        var resolver = Resolver();

        var act = () => resolver.EnsureAvailable(session);

        act.Should().Throw<SessionWorkspaceUnavailableException>()
            .Where(exception =>
                exception.SessionId == session.Id &&
                exception.AcceptedRevision == "sha256:accepted")
            .WithMessage("*was not recreated*restore or reconcile*");
        Directory.Exists(resolver.Resolve(session.Id).HostPath).Should().BeFalse();
    }

    [Fact]
    public void EnsureAvailable_PreservesExistingAcceptedWorkspace()
    {
        var session = Session("sha256:accepted");
        var resolver = Resolver();
        var expected = resolver.Resolve(session.Id);
        Directory.CreateDirectory(expected.HostPath);
        var marker = Path.Combine(expected.HostPath, "keep.txt");
        File.WriteAllText(marker, "accepted");

        var workspace = resolver.EnsureAvailable(session);

        workspace.Should().Be(expected);
        File.ReadAllText(marker).Should().Be("accepted");
    }

    private CodeGenerationWorkspaceResolver Resolver()
    {
        var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".sessions"));
        return new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions
            {
                OutputRoot = rootPath,
                CiRelativePath = "generated"
            }),
            sessionStore);
    }

    private static GuyabanoSession Session(string? currentWorkspaceRevision) =>
        new()
        {
            Id = GuyabanoSessionId.New(),
            RepositoryId = "repo:test",
            WorkspaceId = $"workspace:{Guid.CreateVersion7():D}",
            CreatedAt = DateTimeOffset.UtcNow,
            CurrentWorkspaceRevision = currentWorkspaceRevision
        };

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
