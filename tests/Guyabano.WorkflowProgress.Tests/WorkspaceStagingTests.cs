using FluentAssertions;
using Guyabano.Artifacts;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Session;
using Guyabano.WorkflowWorker;
using Microsoft.Extensions.Options;

namespace Guyabano.WorkflowProgressTests;

public sealed class WorkspaceStagingTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-staging-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ValidateAndPromote_UpdatesWorkspaceAndSessionRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionStore = new FileSystemGuyabanoSessionStore(Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: ct);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var staging = new CodeGenerationStagingService(
            resolver,
            sessionStore,
            new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts")),
            new FileSystemSessionEventStore(Path.Combine(rootPath, ".gen", "session-events")),
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }));

        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);
        await File.WriteAllTextAsync(Path.Combine(workspace.HostPath, "A.cs"), "class A {}", ct);

        // Establish an initial accepted revision
        var initialRevision = await ComputeRevisionAsync(workspace.HostPath, ct);
        await sessionStore.UpdateWorkspaceRevisionAsync(session.Id, null, initialRevision, ct);

        // Create staging from that baseline
        var mutation = await staging.CreateStagingAsync(session.Id.Value, "mut-1", ct);
        mutation.BaselineRevision.Should().Be(initialRevision);
        Directory.Exists(mutation.StagingHostPath).Should().BeTrue();

        // Mutate the staging (simulate generation/repair in isolation)
        await File.WriteAllTextAsync(Path.Combine(mutation.StagingHostPath, "A.cs"), "class A { public int X => 1; }", ct);
        await File.WriteAllTextAsync(Path.Combine(mutation.StagingHostPath, "B.cs"), "class B {}", ct);

        var promotion = await staging.ValidateAndPromoteAsync(
            session.Id.Value,
            "mut-1",
            initialRevision,
            (path, token) => Task.FromResult(new StagingValidationResult(true)),
            ct);

        promotion.FromRevision.Should().Be(initialRevision);
        promotion.Validated.Should().BeTrue();

        var reloaded = await sessionStore.GetAsync(session.Id, ct);
        reloaded!.CurrentWorkspaceRevision.Should().Be(promotion.ToRevision);
        promotion.ToRevision.Should().Be(await ComputeRevisionAsync(workspace.HostPath, ct));

        // Staging no longer exists (promoted), workspace carries new content
        Directory.Exists(Path.Combine(rootPath, ".gen", "sessions", session.Id.ToString(), "staging", "mut-1"))
            .Should().BeFalse();
        (await File.ReadAllTextAsync(Path.Combine(workspace.HostPath, "B.cs"), ct)).Should().Be("class B {}");

        // Promotion artifact persisted
        var artifact = await new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts"))
            .ReadLatestAsync<WorkspacePromotion>(runId.ToString("D"), "workspace-promotion", "mut-1", ct);
        artifact.Should().NotBeNull();
        artifact!.Payload.FromRevision.Should().Be(initialRevision);
        artifact.Payload.ToRevision.Should().Be(promotion.ToRevision);
    }

    [Fact]
    public async Task FirstStaging_EstablishesBaselineWithoutCallerInitialization()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions-first"));
        var session = await sessionStore.CreateAsync(
            "repo:first",
            "workspace:first",
            cancellationToken: ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions
            {
                OutputRoot = rootPath,
                CiRelativePath = "."
            }),
            sessionStore);
        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.HostPath, "A.cs"),
            "class A {}",
            ct);
        var staging = new CodeGenerationStagingService(
            resolver,
            sessionStore,
            new FileSystemArtifactRepository(
                Path.Combine(rootPath, ".gen", "artifacts-first")),
            new FileSystemSessionEventStore(
                Path.Combine(rootPath, ".gen", "events-first")),
            Options.Create(new CodeGenerationWorkerOptions
            {
                OutputRoot = rootPath,
                CiRelativePath = "."
            }));

        var mutation = await staging.CreateStagingAsync(
            session.Id.Value,
            "first",
            ct);

        var reloaded = await sessionStore.GetAsync(session.Id, ct);
        reloaded!.CurrentWorkspaceRevision.Should().Be(mutation.BaselineRevision);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("child/../../escape")]
    [InlineData("child\\..\\escape")]
    public void StagingResolver_RejectsUnsafeMutationId(string mutationId)
    {
        using var sessionStore = new FileSystemGuyabanoSessionStore(
            Path.Combine(rootPath, ".gen", "sessions-path"));
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions
            {
                OutputRoot = rootPath,
                CiRelativePath = "."
            }),
            sessionStore);

        var act = () => resolver.ResolveStaging(
            GuyabanoSessionId.New(),
            mutationId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task PromoteOverChangedBaseline_IsFencedAndRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionStore = new FileSystemGuyabanoSessionStore(Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: ct);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var staging = new CodeGenerationStagingService(
            resolver,
            sessionStore,
            new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts")),
            new FileSystemSessionEventStore(Path.Combine(rootPath, ".gen", "session-events")),
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }));

        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);
        await File.WriteAllTextAsync(Path.Combine(workspace.HostPath, "A.cs"), "class A {}", ct);
        var revisionV1 = await ComputeRevisionAsync(workspace.HostPath, ct);
        await sessionStore.UpdateWorkspaceRevisionAsync(session.Id, null, revisionV1, ct);

        var mutation = await staging.CreateStagingAsync(session.Id.Value, "mut-stale", ct);
        await File.WriteAllTextAsync(Path.Combine(mutation.StagingHostPath, "B.cs"), "class B {}", ct);

        // Another actor advances the workspace first
        await File.WriteAllTextAsync(Path.Combine(workspace.HostPath, "C.cs"), "class C {}", ct);
        var revisionV2 = await ComputeRevisionAsync(workspace.HostPath, ct);
        await sessionStore.UpdateWorkspaceRevisionAsync(session.Id, revisionV1, revisionV2, ct);

        // Promotion with the stale baseline must be fenced and must not alter the workspace
        var act = () => staging.ValidateAndPromoteAsync(
            session.Id.Value,
            "mut-stale",
            revisionV1,
            (path, token) => Task.FromResult(new StagingValidationResult(true)),
            ct);
        await act.Should().ThrowAsync<ConcurrentWorkspaceMutationException>();

        File.Exists(Path.Combine(workspace.HostPath, "C.cs")).Should().BeTrue();
        File.Exists(Path.Combine(workspace.HostPath, "B.cs")).Should().BeFalse();
        (await sessionStore.GetAsync(session.Id, ct))!.CurrentWorkspaceRevision.Should().Be(revisionV2);
    }

    [Fact]
    public async Task FailedValidation_DiscardsStagingAndLeavesWorkspaceUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionStore = new FileSystemGuyabanoSessionStore(Path.Combine(rootPath, ".gen", "sessions"));
        var session = await sessionStore.CreateAsync("repo:test", "workspace:test", cancellationToken: ct);
        var runId = Guid.NewGuid();
        await sessionStore.AttachWorkflowRunAsync(session.Id, runId, ct);
        var resolver = new CodeGenerationWorkspaceResolver(
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }),
            sessionStore);
        var staging = new CodeGenerationStagingService(
            resolver,
            sessionStore,
            new FileSystemArtifactRepository(Path.Combine(rootPath, ".gen", "artifacts")),
            new FileSystemSessionEventStore(Path.Combine(rootPath, ".gen", "session-events")),
            Options.Create(new CodeGenerationWorkerOptions { OutputRoot = rootPath, CiRelativePath = "." }));

        var workspace = resolver.Resolve(session.Id);
        Directory.CreateDirectory(workspace.HostPath);
        await File.WriteAllTextAsync(Path.Combine(workspace.HostPath, "A.cs"), "class A {}", ct);
        var revisionV1 = await ComputeRevisionAsync(workspace.HostPath, ct);
        await sessionStore.UpdateWorkspaceRevisionAsync(session.Id, null, revisionV1, ct);

        var mutation = await staging.CreateStagingAsync(session.Id.Value, "mut-bad", ct);
        await File.WriteAllTextAsync(Path.Combine(mutation.StagingHostPath, "Bad.cs"), "class Bad {", ct);

        var act = () => staging.ValidateAndPromoteAsync(
            session.Id.Value,
            "mut-bad",
            revisionV1,
            (path, token) => Task.FromResult(new StagingValidationResult(false, "compile failed")),
            ct);
        await act.Should().ThrowAsync<StagingValidationException>();

        // Workspace untouched; staging discarded per explicit policy
        File.Exists(Path.Combine(workspace.HostPath, "Bad.cs")).Should().BeFalse();
        Directory.Exists(mutation.StagingHostPath).Should().BeFalse();
        (await sessionStore.GetAsync(session.Id, ct))!.CurrentWorkspaceRevision.Should().Be(revisionV1);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private static async Task<string> ComputeRevisionAsync(string path, CancellationToken ct)
    {
        var snapshot = await GeneratedFileManifestFactory.SnapshotWorkspaceAsync(path, ct);
        var ordered = snapshot
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value.Hash}");
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(string.Join("|", ordered))))
            .ToLowerInvariant();
    }
}
