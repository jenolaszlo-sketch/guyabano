using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class PlanningStageAdapterTests
{
    private static ContentDigest Digest(char c) => new("sha256", "descriptor/v1", new string(c, 64));

    private static DescriptorReference Ref(DescriptorKind kind, string name) =>
        new(kind, name, "1", Digest('a'));

    private static PlanningStageDefinition Definition(string id, int maxAttempts = 3) => new()
    {
        Id = id,
        ArtifactKind = "test-kind",
        SystemPack = $"{id}/system.sbn",
        UserPack = $"{id}/user.sbn",
        InputKinds = [],
        OutputSchema = "Test",
        MaxAttempts = maxAttempts,
        ModelProfile = id,
    };

    private static StageExecutionInput Input(
        string stageName,
        IReadOnlyDictionary<string, JsonElement> upstream,
        string request = "Test the adapters.") => new(
            new PlannedStage
            {
                StageId = "test",
                Name = stageName,
                DependsOn = [],
                InputArtifacts = [],
            },
            Definition("test"),
            upstream,
            request,
            upstream.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());

    private static JsonElement Payload(string text) =>
        JsonSerializer.SerializeToElement(new { note = text });

    private static string ArgText(InferenceExecutionRequest request, string name) =>
        request.Arguments.Single(argument => argument.Name == name) is var argument
            ? RuntimeValueJson.ToJsonElement(argument.Value).GetString()!
            : throw new InvalidOperationException($"Missing argument '{name}'.");

    [Fact]
    public async Task Domain_adapter_binds_the_goal_and_extracts_the_artifact()
    {
        var inner = new CapturingExecutor(_ => Payload("domain-ok"));
        var adapter = new DomainDiscoveryStageAdapter(
            inner, Ref(DescriptorKind.InferenceProfile, "profile"), Ref(DescriptorKind.PromptTemplate, "template"));

        var result = await adapter.ExecuteAsync(
            Input("domain-discovery/main", new Dictionary<string, JsonElement>()),
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Output!.Value.GetProperty("note").GetString().Should().Be("domain-ok");
        inner.Requests.Should().ContainSingle();
        inner.Requests[0].Profile.Name.Should().Be("profile");
        inner.Requests[0].PromptTemplate!.Name.Should().Be("template");
        ArgText(inner.Requests[0], "request").Should().Be("Test the adapters.");
    }

    [Fact]
    public async Task Topology_adapter_binds_the_domain_upstream()
    {
        var inner = new CapturingExecutor(_ => Payload("topology-ok"));
        var adapter = new SolutionTopologyStageAdapter(
            inner, Ref(DescriptorKind.InferenceProfile, "profile"), Ref(DescriptorKind.PromptTemplate, "template"));
        var upstream = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["domain-discovery/main"] = Payload("domain-here"),
        };

        var result = await adapter.ExecuteAsync(
            Input("solution-topology/main", upstream), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        ArgText(inner.Requests.Single(), "request").Should().Be("Test the adapters.");
        RuntimeValueJson.ToJsonElement(
                inner.Requests.Single().Arguments.Single(a => a.Name == "domain").Value)
            .GetProperty("note").GetString().Should().Be("domain-here");
    }

    [Fact]
    public async Task Topology_adapter_fails_without_a_domain_upstream()
    {
        var inner = new CapturingExecutor(_ => Payload("topology-ok"));
        var adapter = new SolutionTopologyStageAdapter(
            inner, Ref(DescriptorKind.InferenceProfile, "profile"), Ref(DescriptorKind.PromptTemplate, "template"));

        var result = await adapter.ExecuteAsync(
            Input("solution-topology/main", new Dictionary<string, JsonElement>()),
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle().Which.Should().Contain("domain-discovery");
        inner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Adapter_retries_through_previous_failure()
    {
        var calls = 0;
        var inner = new CapturingExecutor(_ =>
        {
            calls++;
            if (calls < 3)
                throw new InvalidOperationException($"bad attempt {calls}");
            return Payload("eventual-ok");
        });
        var adapter = new DomainDiscoveryStageAdapter(
            inner, Ref(DescriptorKind.InferenceProfile, "profile"), Ref(DescriptorKind.PromptTemplate, "template"));

        var result = await adapter.ExecuteAsync(
            Input("domain-discovery/main", new Dictionary<string, JsonElement>()),
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Output!.Value.GetProperty("note").GetString().Should().Be("eventual-ok");
        inner.Requests.Should().HaveCount(3);
        ArgText(inner.Requests[0], "previousFailure").Should().BeEmpty();
        ArgText(inner.Requests[2], "previousFailure").Should().Contain("bad attempt 2");
    }

    [Fact]
    public async Task Adapter_reports_exhaustion()
    {
        var inner = new CapturingExecutor(_ => throw new InvalidOperationException("always bad"));
        var adapter = new DomainDiscoveryStageAdapter(
            inner, Ref(DescriptorKind.InferenceProfile, "profile"), Ref(DescriptorKind.PromptTemplate, "template"));

        var result = await adapter.ExecuteAsync(
            Input("domain-discovery/main", new Dictionary<string, JsonElement>()),
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().HaveCount(3);
        inner.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Contract_bundle_orders_upstream_by_availability_not_alphabet()
    {
        var inner = new CapturingExecutor(_ => Payload("catalog-ok"));
        var adapter = new ContractDesignStageAdapter(
            inner, Ref(DescriptorKind.InferenceProfile, "profile"), Ref(DescriptorKind.PromptTemplate, "template"));
        var topology = new SolutionTopology
        {
            Solution = new PlannedSolution { Name = "App", Path = "App.slnx" },
            Projects = [],
            BoundedContexts =
            [
                new BoundedContextPlan
                {
                    Name = "Zeta",
                    Purpose = "Z.",
                    CapabilityNames = [],
                    DependsOnContextNames = [],
                    InboundAdapters = [],
                    OutboundAdapters = [],
                },
                new BoundedContextPlan
                {
                    Name = "Beta",
                    Purpose = "B.",
                    CapabilityNames = [],
                    DependsOnContextNames = [],
                    InboundAdapters = [],
                    OutboundAdapters = [],
                },
                new BoundedContextPlan
                {
                    Name = "Alpha",
                    Purpose = "A.",
                    CapabilityNames = [],
                    DependsOnContextNames = ["Zeta", "Beta"],
                    InboundAdapters = [],
                    OutboundAdapters = [],
                },
            ],
            Modules = [],
            Decisions = [],
        };
        JsonElement CatalogFor(string context) =>
            JsonSerializer.SerializeToElement(new BoundedContextContractCatalog
            {
                BoundedContextName = context,
                Contracts = [],
                Decisions = [],
                InferredDefaults = [],
            });
        var upstream = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["domain-discovery/main"] = Payload("domain"),
            ["solution-topology/main"] = JsonSerializer.SerializeToElement(topology),
            ["contracts/zeta"] = CatalogFor("Zeta"),
            ["contracts/beta"] = CatalogFor("Beta"),
        };
        var input = new StageExecutionInput(
            new PlannedStage
            {
                StageId = "contract-design",
                Name = "contracts/alpha",
                DependsOn = ["contracts/zeta", "contracts/beta"],
                InputArtifacts = [],
            },
            Definition("contract-design"),
            upstream,
            "Test the adapters.",
            ["domain-discovery/main", "solution-topology/main", "contracts/zeta", "contracts/beta"]);

        var result = await adapter.ExecuteAsync(input, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        var bundle = RuntimeValueJson.ToJsonElement(
            inner.Requests.Single().Arguments.Single(a => a.Name == "bundle").Value);
        bundle.GetProperty("upstreamCatalogs").EnumerateArray()
            .Select(element => element.GetProperty("boundedContextName").GetString())
            .Should().Equal("Zeta", "Beta");
    }

    private sealed class CapturingExecutor(Func<InferenceExecutionRequest, JsonElement> produce)
        : IInferenceExecutor
    {
        public List<InferenceExecutionRequest> Requests { get; } = [];

        public async ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            return InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(produce(request)));
        }
    }
}
