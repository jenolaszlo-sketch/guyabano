#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.Llm.Prompting;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Gap B proposer: a model proposes a workflow patch as JSON against a known
/// design base, and deterministic validation (provenance plus Apply) owns
/// admission with repair feedback.
/// </summary>
public sealed class WorkflowPatchProposerTests
{
    private static ContentDigest Digest(char c) => new("sha256", "descriptor/v1", new string(c, 64));

    private static DescriptorReference ActivityRef(string version, char digest) =>
        new(DescriptorKind.Activity, "guyabano.execute", version, Digest(digest));

    private static PlannedExecutionDesign Design()
    {
        var steps = new[]
        {
            new PlannedExecutionStep
            {
                Id = "implement_a",
                Title = "Implement todos",
                DependsOn = [],
                RequiredArtifacts = [],
                AcceptanceCriteria = [],
            },
            new PlannedExecutionStep
            {
                Id = "implement_billing",
                Title = "Implement billing",
                DependsOn = ["implement_a"],
                RequiredArtifacts = ["contracts/billing@1"],
                AcceptanceCriteria = [],
            },
        };
        var bindings = new[]
        {
            new PlannedNodeBinding
            {
                StepId = "implement_a",
                Binding = new PlannedExecutionBinding
                {
                    Role = "implement",
                    Capability = "code.modify",
                    ModelProfile = "implementation",
                    ContextArtifacts = [],
                    Descriptor = ActivityRef("1", 'a'),
                },
            },
            new PlannedNodeBinding
            {
                StepId = "implement_billing",
                Binding = new PlannedExecutionBinding
                {
                    Role = "implement",
                    Capability = "code.modify",
                    ModelProfile = "implementation",
                    ContextArtifacts = ["contracts/billing@1"],
                    Descriptor = ActivityRef("1", 'a'),
                },
            },
        };
        return new PlannedExecutionDesign(
            new PlannedExecutionGraph
            {
                WorkflowName = "implementation",
                InputType = "string",
                OutputType = "string",
                Steps = steps,
            },
            new PlannedExecutionBindings
            {
                WorkflowName = "implementation",
                Nodes = bindings,
            });
    }

    private static WorkflowPatch BillingV2Patch(PlannedExecutionDesign design)
    {
        var billing = design.Bindings.Nodes.Single(node => node.StepId == "implement_billing");
        return new WorkflowPatch
        {
            BaseDesignFingerprint = StagedExecutionDesignIdentity.Compute(design),
            DerivedFromArtifacts = ["contracts/billing@2"],
            Rationale = "Use the v2 generator for billing.",
            AddSteps = [],
            ReplaceSteps = [],
            RemoveStepIds = [],
            AddBindings = [],
            ReplaceBindings =
            [
                billing with
                {
                    Binding = billing.Binding with { Descriptor = ActivityRef("2", 'b') },
                },
            ],
            DependencyEdits = [],
        };
    }

    [Fact]
    public async Task Proposer_pack_renders_the_base_and_change_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var baseFingerprint = StagedExecutionDesignIdentity.Compute(design);

        var promptsRoot = FindPromptsRoot();
        var builder = new WorkflowPatchPromptBuilder(
            new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot)));
        var request = await builder.BuildAsync(
            new WorkflowPatchPromptContext(
                "Use the v2 generator for billing.",
                PlannedExecutionDesignSummary.Render(design),
                baseFingerprint,
                ["contracts/billing@2"],
                "activity guyabano.execute@1#aaa",
                4000),
            ct);

        var systemText = TextOf(request, "system");
        var userText = TextOf(request, "user");
        systemText.Should().Contain("baseDesignFingerprint");
        systemText.Should().Contain(baseFingerprint);
        systemText.Should().Contain("contracts/billing@2");
        systemText.Should().Contain("implement_billing");
        userText.Should().Contain("Use the v2 generator for billing.");
        userText.Should().Contain("exactly one JSON object");
    }

    [Fact]
    public async Task Propose_accepts_a_valid_patch_and_applies_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var patchJson = JsonSerializer.Serialize(BillingV2Patch(design));

        var router = new ScriptedRouter([patchJson]);
        var proposer = new WorkflowPatchProposer(
            router,
            new WorkflowPatchPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()))),
            maxAttempts: 3);

        var result = await proposer.ProposeAsync(
            "Use the v2 generator for billing.",
            design,
            PlannedExecutionDesignSummary.Render(design),
            ["contracts/billing@2"],
            "activity guyabano.execute@1#aaa",
            "stub-proposer", 4000, cancellationToken: ct);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Attempts.Should().HaveCount(1);
        result.Applied!.Bindings.Nodes.Single(node => node.StepId == "implement_billing")
            .Binding.Descriptor.Version.Should().Be("2");
        result.Applied.Graph.Steps.Select(step => step.Id).Should()
            .BeEquivalentTo("implement_a", "implement_billing");
    }

    [Fact]
    public async Task Propose_canonicalizes_binding_digests_from_the_catalogue()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var catalogue = new InMemoryTrustedCatalogue(
        [
            new TrustedCatalogueDescriptor(ActivityRef("1", 'a')),
            new TrustedCatalogueDescriptor(ActivityRef("2", 'b')),
        ]);
        var mangled = BillingV2Patch(design);
        var billing = mangled.ReplaceBindings.Single();
        var bad = billing with
        {
            Binding = billing.Binding with
            {
                Descriptor = new DescriptorReference(
                    DescriptorKind.Activity,
                    "guyabano.execute",
                    "2",
                    new ContentDigest("sha256", "descriptor/v1", "invented")),
            },
        };
        var patchJson = JsonSerializer.Serialize(mangled with { ReplaceBindings = [bad] });

        var router = new ScriptedRouter([patchJson]);
        var proposer = new WorkflowPatchProposer(
            router,
            new WorkflowPatchPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()))),
            maxAttempts: 1);

        var result = await proposer.ProposeAsync(
            "Use the v2 generator for billing.",
            design,
            PlannedExecutionDesignSummary.Render(design),
            ["contracts/billing@2"],
            "activity guyabano.execute@1#aaa",
            "stub-proposer",
            4000,
            catalogue,
            cancellationToken: ct);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Applied!.Bindings.Nodes.Single(node => node.StepId == "implement_billing")
            .Binding.Descriptor.ContentDigest.Value.Should().Be(new string('b', 64));
    }

    [Fact]
    public async Task Propose_rejects_bindings_unknown_to_the_catalogue()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var catalogue = new InMemoryTrustedCatalogue(
        [
            new TrustedCatalogueDescriptor(ActivityRef("1", 'a')),
        ]);
        var mangled = BillingV2Patch(design);
        var billing = mangled.ReplaceBindings.Single();
        var bad = billing with
        {
            Binding = billing.Binding with
            {
                Descriptor = new DescriptorReference(
                    DescriptorKind.Activity,
                    "guyabano.missing",
                    "9",
                    new ContentDigest("sha256", "descriptor/v1", "invented")),
            },
        };
        var patchJson = JsonSerializer.Serialize(mangled with { ReplaceBindings = [bad] });

        var router = new ScriptedRouter([patchJson]);
        var proposer = new WorkflowPatchProposer(
            router,
            new WorkflowPatchPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()))),
            maxAttempts: 1);

        var result = await proposer.ProposeAsync(
            "Use the v2 generator for billing.",
            design,
            PlannedExecutionDesignSummary.Render(design),
            ["contracts/billing@2"],
            "activity guyabano.execute@1#aaa",
            "stub-proposer",
            4000,
            catalogue,
            cancellationToken: ct);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Contain("unknown descriptor");
    }

    [Fact]
    public async Task Propose_repairs_invalid_json_and_stale_bases()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var stale = BillingV2Patch(design) with
        {
            BaseDesignFingerprint = "sha256:execution-design/v1:stale",
        };

        var router = new ScriptedRouter(
        [
            "{ not json",
            JsonSerializer.Serialize(stale),
            JsonSerializer.Serialize(BillingV2Patch(design)),
        ]);
        var proposer = new WorkflowPatchProposer(
            router,
            new WorkflowPatchPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()))),
            maxAttempts: 3);

        var result = await proposer.ProposeAsync(
            "Use the v2 generator for billing.",
            design,
            PlannedExecutionDesignSummary.Render(design),
            ["contracts/billing@2"],
            "activity guyabano.execute@1#aaa",
            "stub-proposer", 4000, cancellationToken: ct);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics));
        result.Attempts.Should().HaveCount(3);
        result.Attempts[0].Diagnostics.Should().ContainSingle()
            .Which.Should().Contain("not a valid workflow patch");
        result.Attempts[1].Diagnostics.Should().ContainSingle()
            .Which.Should().Contain("stale");
        TextOf(router.Requests[2], "user").Should().Contain("stale");
    }

    [Fact]
    public async Task Propose_rejects_provenance_outside_the_change_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var design = Design();
        var outside = BillingV2Patch(design) with
        {
            DerivedFromArtifacts = ["contracts/unrelated@9"],
        };

        var router = new ScriptedRouter(
        [
            JsonSerializer.Serialize(outside),
            JsonSerializer.Serialize(outside),
        ]);
        var proposer = new WorkflowPatchProposer(
            router,
            new WorkflowPatchPromptBuilder(
                new ScribanPromptTemplateEngine(new FilePromptLoader(FindPromptsRoot()))),
            maxAttempts: 2);

        var result = await proposer.ProposeAsync(
            "Use the v2 generator for billing.",
            design,
            PlannedExecutionDesignSummary.Render(design),
            ["contracts/billing@2"],
            "activity guyabano.execute@1#aaa",
            "stub-proposer", 4000, cancellationToken: ct);

        result.Succeeded.Should().BeFalse();
        result.Attempts.Should().HaveCount(2);
        result.Attempts.SelectMany(attempt => attempt.Diagnostics)
            .Should().OnlyContain(diagnostic => diagnostic.Contains("outside the supplied change set"));
    }

    private static string TextOf(LlmRequest request, string role) => string.Concat(request.Messages
        .Where(m => m.Role == role)
        .SelectMany(m => m.Parts)
        .OfType<LlmTextContent>()
        .Select(p => p.Text));

    private static string FindPromptsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "prompts", "workflow-patch", "system.sbn");
            if (File.Exists(candidate))
                return Path.Combine(directory.FullName, "prompts");
        }
        throw new DirectoryNotFoundException("Could not locate the Guyabano prompts root.");
    }

    private sealed class ScriptedRouter(IReadOnlyList<string> script) : ILlmRouter
    {
        private int next;
        public List<LlmRequest> Requests { get; } = [];

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var index = Math.Min(next, script.Count - 1);
            next++;
            return StreamSingle(script[index]);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ModelStrategy strategy, LlmRequest request, CancellationToken cancellationToken) =>
            StreamAsync(strategy.ToString(), request, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(model, builder.Build(ModelStrategy.Auto), cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ModelStrategy strategy, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(strategy.ToString(), builder, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
            string route, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(route, builder, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
            string route, LlmRequest request, CancellationToken cancellationToken) =>
            StreamAsync(route, request, cancellationToken);

        public ResolvedEndpoint Resolve(string model) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(string model, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ResolvedEndpoint Resolve(ModelStrategy strategy) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(ModelStrategy strategy, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveRouteAsync(string route, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainModelAsync(string model, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainStrategyAsync(ModelStrategy strategy, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainRouteAsync(string route, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        private static async IAsyncEnumerable<LlmStreamEvent> StreamSingle(
            string delta,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new LlmStreamEvent(delta, null, "stop", null, null, null, null, null, null);
        }
    }
}
