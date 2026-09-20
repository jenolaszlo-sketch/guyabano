using FluentAssertions;
using Penghou.Guihua.Baize;
using Penghou.Guihua;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class StagedPlanningArtifactPublisherTests : IDisposable
{
    private const string WorkflowId = "planning-workflow-1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-staged-publisher-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublishAsync_AssignsStableIdentitiesAndDeclaredInputs()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var publisher = new StagedPlanningArtifactPublisher(catalog);

        var versions = await publisher.PublishAsync(
            WorkflowId,
            CreateArtifacts("Manage todos."),
            sessionId: "session-1",
            cancellationToken: ct);

        versions.Domain.Value.Should().Be("domain-discovery/main@1");
        versions.Topology.Value.Should().Be("solution-topology/main@1");
        versions.Contracts["Todos"].Value.Should().Be("contracts/todos@1");
        versions.Contracts["Billing"].Value.Should().Be("contracts/billing@1");
        versions.Components["Billing"].Value.Should().Be("components/billing@1");

        var topology = await catalog.GetCurrentAsync(
            WorkflowId,
            new PlanningArtifactKey(StagedPlanningArtifactPublisher.TopologyKind, "main"),
            ct);
        topology!.Inputs.Select(version => version.Value)
            .Should().Equal("domain-discovery/main@1");
        topology.ProducedBy.Should().Be(StagedPlanningArtifactPublisher.PlanTopology);
        topology.SessionId.Should().Be("session-1");

        var todosContract = await catalog.GetCurrentAsync(
            WorkflowId,
            StagedPlanningArtifactPublisher.ContextKey(
                StagedPlanningArtifactPublisher.ContractKind, "Todos"),
            ct);
        todosContract!.Inputs.Select(version => version.Value)
            .Should().Equal("solution-topology/main@1");

        var billingContract = await catalog.GetCurrentAsync(
            WorkflowId,
            StagedPlanningArtifactPublisher.ContextKey(
                StagedPlanningArtifactPublisher.ContractKind, "Billing"),
            ct);
        billingContract!.Inputs.Select(version => version.Value)
            .Should().BeEquivalentTo("solution-topology/main@1", "contracts/todos@1");

        var billingComponents = await catalog.GetCurrentAsync(
            WorkflowId,
            StagedPlanningArtifactPublisher.ContextKey(
                StagedPlanningArtifactPublisher.ComponentKind, "Billing"),
            ct);
        billingComponents!.Inputs.Select(version => version.Value)
            .Should().Equal("contracts/billing@1");
    }

    [Fact]
    public async Task PublishAsync_RoundTripsTheStoredPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var publisher = new StagedPlanningArtifactPublisher(catalog);

        var versions = await publisher.PublishAsync(
            WorkflowId,
            CreateArtifacts("Manage todos."),
            cancellationToken: ct);

        var record = await catalog.GetAsync(WorkflowId, versions.Domain, ct);
        var payload = await catalog.ReadPayloadAsync<DomainDiscovery>(record!, ct);

        payload.Title.Should().Be("Todo API");
    }

    [Fact]
    public async Task InvalidateAsync_AfterTopologyRevision_MarksOnlyTheDownstreamPlanningRegionStale()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = CreateCatalog();
        var publisher = new StagedPlanningArtifactPublisher(catalog);
        var baseline = await publisher.PublishAsync(
            WorkflowId,
            CreateArtifacts("Manage todos."),
            cancellationToken: ct);

        // Revise only the topology, as a real replan of one artifact would.
        var revisedTopology = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<SolutionTopology>(
                WorkflowId,
                new PlanningArtifactKey(StagedPlanningArtifactPublisher.TopologyKind, "main"),
                1,
                StagedPlanningArtifactPublisher.PlanTopology,
                CreateTopology("Manage todos with caching."),
                Inputs: [baseline.Domain],
                State: PlanningArtifactState.Valid),
            ct);

        var impact = await catalog.InvalidateAsync(WorkflowId, revisedTopology.Version, ct);

        impact.Affected.Select(record => record.Version.Value).Should().BeEquivalentTo(
            "solution-topology/main@1",
            "contracts/todos@1",
            "contracts/billing@1",
            "components/todos@1",
            "components/billing@1");

        (await catalog.GetAsync(WorkflowId, baseline.Contracts["Todos"], ct))!
            .State.Should().Be(PlanningArtifactState.Stale);
        (await catalog.GetAsync(WorkflowId, baseline.Components["Billing"], ct))!
            .State.Should().Be(PlanningArtifactState.Stale);
        (await catalog.GetAsync(WorkflowId, baseline.Domain, ct))!
            .State.Should().Be(PlanningArtifactState.Valid);
        (await catalog.GetAsync(WorkflowId, revisedTopology.Version, ct))!
            .State.Should().Be(PlanningArtifactState.Valid);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private PlanningArtifactCatalog CreateCatalog() =>
        new(new FileSystemArtifactRepository(_root));

    private static StagedPlanningArtifacts CreateArtifacts(string topologyPurpose) =>
        new(
            CreateDomain(),
            CreateTopology(topologyPurpose),
            [
                new BoundedContextContractCatalog
                {
                    BoundedContextName = "Todos",
                    Contracts = [],
                    Decisions = [],
                    InferredDefaults = [],
                },
                new BoundedContextContractCatalog
                {
                    BoundedContextName = "Billing",
                    Contracts = [],
                    Decisions = [],
                    InferredDefaults = [],
                },
            ],
            [
                new BoundedContextComponentManifest
                {
                    BoundedContextName = "Todos",
                    Components = [],
                    Decisions = [],
                    InferredDefaults = [],
                },
                new BoundedContextComponentManifest
                {
                    BoundedContextName = "Billing",
                    Components = [],
                    Decisions = [],
                    InferredDefaults = [],
                },
            ]);

    private static DomainDiscovery CreateDomain() =>
        new()
        {
            Mission = new ProductMission
            {
                GuidingIntent = "Track todos.",
                SuccessOutcomes = [],
                Constraints = [],
                NonGoals = [],
            },
            Title = "Todo API",
            Summary = "Tracks todos.",
            Terms = [],
            Capabilities = [],
            UseCases = [],
            QualityAttributes = [],
            Assumptions = [],
            InferredDefaults = [],
            ProductAmbiguities = [],
        };

    private static SolutionTopology CreateTopology(string todosPurpose) =>
        new()
        {
            Solution = new PlannedSolution { Name = "TodoApi", Path = "TodoApi.sln" },
            Projects = [],
            BoundedContexts =
            [
                new BoundedContextPlan
                {
                    Name = "Todos",
                    Purpose = todosPurpose,
                    CapabilityNames = [],
                    DependsOnContextNames = [],
                    InboundAdapters = [],
                    OutboundAdapters = [],
                },
                new BoundedContextPlan
                {
                    Name = "Billing",
                    Purpose = "Bill for todos.",
                    CapabilityNames = [],
                    DependsOnContextNames = ["Todos"],
                    InboundAdapters = [],
                    OutboundAdapters = [],
                },
            ],
            Modules = [],
            Decisions = [],
        };
}
