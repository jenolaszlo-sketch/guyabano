using FluentAssertions;

namespace Guyabano.Artifacts.Tests;

public sealed class PlanningArtifactCatalogTests : IDisposable
{
    private const string WorkflowId = "planning-workflow-1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-planning-catalog-tests",
        Guid.NewGuid().ToString("N"));

    private PlanningArtifactCatalog CreateCatalog() =>
        new(new FileSystemArtifactRepository(_root));

    [Fact]
    public async Task PublishAsync_AssignsStableIdentityAndIncrementingRevisions()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var key = new PlanningArtifactKey("contracts", "TicketClassifier");

        var first = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId,
                key,
                1,
                "plan-contracts",
                new PlanningPayload("classify(Ticket)"),
                State: PlanningArtifactState.Valid)
            {
                SessionId = "session-1",
            },
            ct);
        var second = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId,
                key,
                1,
                "plan-contracts",
                new PlanningPayload("classify(Ticket, Category)"),
                State: PlanningArtifactState.Valid),
            ct);

        first.Revision.Should().Be(1);
        second.Revision.Should().Be(2);
        second.Key.Should().Be(key);
        second.Key.Value.Should().Be("contracts/TicketClassifier");
        second.Version.Value.Should().Be("contracts/TicketClassifier@2");
        second.ProducedBy.Should().Be("plan-contracts");
        second.SessionId.Should().BeNull();

        var current = await catalog.GetCurrentAsync(WorkflowId, key, ct);
        current!.Version.Should().Be(second.Version);

        var revisions = await catalog.ListRevisionsAsync(WorkflowId, key, ct);
        revisions.Select(record => record.Revision).Should().Equal(1, 2);
        revisions[0].State.Should().Be(PlanningArtifactState.Superseded);
        revisions[1].State.Should().Be(PlanningArtifactState.Valid);
    }

    [Fact]
    public async Task PublishAsync_ReadsBackTheExactPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var payload = new PlanningPayload("governed");

        var record = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId,
                new PlanningArtifactKey("requirements", "goal"),
                1,
                "interpret-goal",
                payload),
            ct);

        var loaded = await catalog.ReadPayloadAsync<PlanningPayload>(record, ct);
        loaded.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task PublishAsync_RecordsUpstreamProvenance()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var architectureKey = new PlanningArtifactKey("architecture", "classification");
        var architecture = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId,
                architectureKey,
                1,
                "plan-architecture",
                new PlanningPayload("SupportApi")),
            ct);

        var contract = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId,
                new PlanningArtifactKey("contracts", "TicketClassifier"),
                1,
                "plan-contracts",
                new PlanningPayload("classify"),
                Inputs: [architecture.Version]),
            ct);

        contract.Inputs.Should().Equal(architecture.Version);

        var envelope = await new FileSystemArtifactRepository(_root)
            .ReadAsync<PlanningPayload>(contract.Content, ct);
        envelope!.Inputs.Select(input => input.ArtifactId)
            .Should().Equal(architecture.Content.ArtifactId);
    }

    [Fact]
    public async Task PublishAsync_ThrowsWhenDeclaredInputVersionIsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var missing = new PlanningArtifactVersion(
            new PlanningArtifactKey("architecture", "absent"),
            1);

        var action = () => catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId,
                new PlanningArtifactKey("contracts", "TicketClassifier"),
                1,
                "plan-contracts",
                new PlanningPayload("classify"),
                Inputs: [missing]),
            ct);

        await action.Should().ThrowAsync<PlanningArtifactNotFoundException>();
    }

    [Fact]
    public async Task InvalidateAsync_MarksOnlyAffectedDescendantsStale()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var architectureKey = new PlanningArtifactKey("architecture", "classification");
        var contractKey = new PlanningArtifactKey("contracts", "TicketClassifier");
        var workUnitKey = new PlanningArtifactKey("work-units", "implement-classifier");
        var unrelatedKey = new PlanningArtifactKey("architecture", "billing");

        var architecture = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, architectureKey, 1, "plan-architecture",
                new PlanningPayload("v1"), State: PlanningArtifactState.Valid), ct);
        var contract = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, contractKey, 1, "plan-contracts",
                new PlanningPayload("c1"), Inputs: [architecture.Version],
                State: PlanningArtifactState.Valid), ct);
        var workUnit = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, workUnitKey, 1, "decompose-work",
                new PlanningPayload("w1"), Inputs: [contract.Version],
                State: PlanningArtifactState.Valid), ct);
        var unrelated = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, unrelatedKey, 1, "plan-architecture",
                new PlanningPayload("billing"), State: PlanningArtifactState.Valid), ct);

        var changed = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, architectureKey, 1, "plan-architecture",
                new PlanningPayload("v2"), State: PlanningArtifactState.Valid), ct);

        var impact = await catalog.InvalidateAsync(WorkflowId, changed.Version, ct);

        impact.Root.Should().Be(changed.Version);
        impact.Affected.Select(record => record.Version.Value)
            .Should().BeEquivalentTo(
                "architecture/classification@1",
                "contracts/TicketClassifier@1",
                "work-units/implement-classifier@1");

        (await catalog.GetAsync(WorkflowId, contract.Version, ct))!
            .State.Should().Be(PlanningArtifactState.Stale);
        (await catalog.GetAsync(WorkflowId, workUnit.Version, ct))!
            .State.Should().Be(PlanningArtifactState.Stale);
        (await catalog.GetAsync(WorkflowId, changed.Version, ct))!
            .State.Should().Be(PlanningArtifactState.Valid);
        (await catalog.GetAsync(WorkflowId, unrelated.Version, ct))!
            .State.Should().Be(PlanningArtifactState.Valid);
    }

    [Fact]
    public async Task InvalidateAsync_ThrowsForUnknownRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var unknown = new PlanningArtifactVersion(
            new PlanningArtifactKey("architecture", "unknown"),
            1);

        var action = () => catalog.InvalidateAsync(WorkflowId, unknown, ct);

        await action.Should().ThrowAsync<PlanningArtifactNotFoundException>();
    }

    [Fact]
    public async Task ListCurrentAsync_ReturnsLatestRevisionPerIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var key = new PlanningArtifactKey("contracts", "TicketClassifier");
        await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, key, 1, "plan-contracts", new PlanningPayload("v1")), ct);
        await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, key, 1, "plan-contracts", new PlanningPayload("v2")), ct);
        await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, new PlanningArtifactKey("architecture", "classification"),
                1, "plan-architecture", new PlanningPayload("arch")), ct);

        var current = await catalog.ListCurrentAsync(WorkflowId, ct);

        current.Should().HaveCount(2);
        current.Single(record => record.Key == key).Revision.Should().Be(2);
    }

    [Fact]
    public async Task RevalidateAsync_RestoresAStaleRevisionToValid()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var architectureKey = new PlanningArtifactKey("architecture", "classification");
        var contractKey = new PlanningArtifactKey("contracts", "TicketClassifier");
        var architecture = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, architectureKey, 1, "plan-architecture",
                new PlanningPayload("v1"), State: PlanningArtifactState.Valid), ct);
        var contract = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, contractKey, 1, "plan-contracts",
                new PlanningPayload("c1"), Inputs: [architecture.Version],
                State: PlanningArtifactState.Valid), ct);
        var changed = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlanningPayload>(
                WorkflowId, architectureKey, 1, "plan-architecture",
                new PlanningPayload("v2"), State: PlanningArtifactState.Valid), ct);

        await catalog.InvalidateAsync(WorkflowId, changed.Version, ct);
        (await catalog.GetAsync(WorkflowId, contract.Version, ct))!
            .State.Should().Be(PlanningArtifactState.Stale);

        var restored = await catalog.RevalidateAsync(WorkflowId, contract.Version, ct);

        restored.State.Should().Be(PlanningArtifactState.Valid);
        (await catalog.GetAsync(WorkflowId, contract.Version, ct))!
            .State.Should().Be(PlanningArtifactState.Valid);
    }

    [Fact]
    public async Task GetCurrentAsync_ReturnsNullForUnknownIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();

        var current = await catalog.GetCurrentAsync(
            WorkflowId,
            new PlanningArtifactKey("contracts", "absent"),
            ct);

        current.Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record PlanningPayload(string Value);
}
