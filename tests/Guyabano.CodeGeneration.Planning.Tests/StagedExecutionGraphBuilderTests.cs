using FluentAssertions;
using Penghou.Guihua;
using Penghou.Guihua.Baize;
using Guyabano.Artifacts;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class StagedExecutionGraphBuilderTests : IDisposable
{
    private const string WorkflowId = "planning-workflow-1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "guyabano-execution-graph-tests",
        Guid.NewGuid().ToString("N"));

    private static ContentDigest Digest(char c) => new("sha256", "descriptor/v1", new string(c, 64));

    private static TrustedCatalogueDescriptor ActivityDescriptor(string name, string parameter)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new TrustedCatalogueDescriptor(
            new DescriptorReference(DescriptorKind.Activity, name, "1", Digest(name == "guyabano.generate" ? 'f' : name == "guyabano.scaffold" ? 'd' : 'e')),
            callableContract: new CallableContract(
                new CallableSignature([new CallableParameter(parameter, str)], str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe));
    }

    private static IReadOnlyDictionary<string, TrustedCatalogueDescriptor> Registry() =>
        new Dictionary<string, TrustedCatalogueDescriptor>(StringComparer.Ordinal)
        {
            [PlannedExecutionRoles.Implement] = ActivityDescriptor("guyabano.generate", "task"),
            [PlannedExecutionRoles.Integrate] = ActivityDescriptor("guyabano.scaffold", "plan"),
            [PlannedExecutionRoles.Verify] = ActivityDescriptor("guyabano.build", "artifact"),
        };

    [Fact]
    public async Task Build_ProducesOrderedPinnedDesignWithResolvedDescriptors()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new PlanningArtifactCatalog(new FileSystemArtifactRepository(_root));
        var publisher = new StagedPlanningArtifactPublisher(catalog);
        var artifacts = CreateArtifacts();
        var versions = await publisher.PublishAsync(WorkflowId, artifacts, cancellationToken: ct);

        var design = StagedExecutionGraphBuilder.Build(artifacts, versions, Registry());

        design.Graph.WorkflowName.Should().Be("implementation");
        design.Graph.Steps.Select(step => step.Id).Should().Equal(
            "implement_todos", "implement_billing", "integrate", "test");
        design.Graph.Steps.Single(step => step.Id == "implement_billing")
            .DependsOn.Should().Equal("implement_todos");
        design.Graph.Steps.Single(step => step.Id == "integrate")
            .DependsOn.Should().Equal("implement_todos", "implement_billing");
        design.Graph.Steps.Single(step => step.Id == "test")
            .DependsOn.Should().Equal("integrate");
        design.Graph.Steps.Single(step => step.Id == "implement_billing")
            .RequiredArtifacts.Should().Equal("contracts/billing@1");
        design.Graph.Steps.Single(step => step.Id == "test")
            .RequiredArtifacts.Should().Equal("components/todos@1", "components/billing@1");

        var bindingByStep = design.Bindings.Nodes.ToDictionary(node => node.StepId);
        bindingByStep["implement_todos"].Binding.Descriptor.ContentDigest.Value
            .Should().Be(new string('f', 64));
        bindingByStep["integrate"].Binding.Descriptor.ContentDigest.Value
            .Should().Be(new string('d', 64));
        bindingByStep["test"].Binding.Descriptor.ContentDigest.Value
            .Should().Be(new string('e', 64));
        bindingByStep["implement_todos"].Binding.Capability.Should().Be("code.modify");
        bindingByStep["implement_todos"].Binding.ModelProfile.Should().Be("implementation");
        bindingByStep["test"].Binding.Capability.Should().Be("process.execute");
        bindingByStep["test"].Binding.ModelProfile.Should().Be("verification");
        bindingByStep["implement_billing"].Binding.ContextArtifacts.Should().Contain(
            "contracts/billing@1");
    }

    [Fact]
    public async Task PublishExecutionDesign_StoresGraphThenBindings()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new PlanningArtifactCatalog(new FileSystemArtifactRepository(_root));
        var publisher = new StagedPlanningArtifactPublisher(catalog);
        var artifacts = CreateArtifacts();
        var versions = await publisher.PublishAsync(WorkflowId, artifacts, cancellationToken: ct);
        var design = StagedExecutionGraphBuilder.Build(artifacts, versions, Registry());

        var designInputs = versions.Contracts.Values
            .Concat(versions.Components.Values)
            .ToArray();
        var published = await publisher.PublishExecutionDesignAsync(
            WorkflowId, design, designInputs, cancellationToken: ct);

        published.Graph.Value.Should().Be("execution-graph/main@1");
        published.Bindings.Value.Should().Be("bindings/main@1");
        var graphRecord = await catalog.GetAsync(WorkflowId, published.Graph, ct);
        graphRecord!.Inputs.Select(version => version.Value).Should().BeEquivalentTo(
            designInputs.Select(version => version.Value));
        var bindingsRecord = await catalog.GetAsync(WorkflowId, published.Bindings, ct);
        bindingsRecord!.Inputs.Select(version => version.Value).Should().Equal(published.Graph.Value);

        var graphPayload = await catalog.ReadPayloadAsync<PlanningGraph>(graphRecord, ct);
        graphPayload.Steps.Should().HaveCount(4);
        var bindingsPayload = await catalog.ReadPayloadAsync<PlanningBindings>(bindingsRecord, ct);
        bindingsPayload.Nodes.Should().HaveCount(4);
    }

    [Fact]
    public void StepId_DerivesFuwenSafeIdentifier()
    {
        StagedExecutionGraphBuilder.StepId("implement", "Ticket Classification")
            .Should().Be("implement_ticket_classification");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static StagedPlanningArtifacts CreateArtifacts() =>
        new(
            new DomainDiscovery
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
            },
            new SolutionTopology
            {
                Solution = new PlannedSolution { Name = "TodoApi", Path = "TodoApi.sln" },
                Projects = [],
                BoundedContexts =
                [
                    new BoundedContextPlan
                    {
                        Name = "Todos",
                        Purpose = "Manage todos.",
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
            },
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
}
