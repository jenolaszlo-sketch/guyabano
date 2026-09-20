using System.Text.Json;
using Penghou.Guihua.Baize;
using Penghou.Guihua;
using Guyabano.Artifacts;
using Guyabano.Llm.Prompting;
using Microsoft.Extensions.Options;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// <see cref="ICodeGenerationPlanningService"/> implemented as two generic
/// stage-runner invocations over the built-in software definitions instead
/// of hand-wired phase workflows: domain plus topology first (the topology
/// is needed to name the per-context instances), then one contract plus
/// component instance per bounded context in topological order with true
/// dependency edges, then plan assembly. Per-stage packs, validators, and
/// retry budgets are unchanged; only orchestration moved.
/// </summary>
/// <remarks>
/// Failure-path repair uses per-stage <c>previousFailure</c> retry rather
/// than the joint assess/gap loop; the joint machinery stays covered by the
/// hand-built joint tests. The outer previousFailure seed is accepted but
/// no longer threaded: first attempts already carry an empty failure, and
/// re-planning starts from artifacts, not error text.
/// </remarks>
public sealed class FuwenStagedPlanningService(
    ILlmRouter llmRouter,
    IPromptBuilder<DomainDiscoveryPromptContext> domainBuilder,
    IPromptBuilder<SolutionTopologyPromptContext> topologyBuilder,
    IPromptBuilder<ContractDesignPromptContext> contractBuilder,
    IPromptBuilder<ComponentDesignPromptContext> componentBuilder,
    IPromptBuilder<PlanningGapResolutionPromptContext> gapBuilder,
    ILlmStructuredOutputRepairer repairer,
    IPromptLoader promptLoader,
    IOptions<FuwenPlanningOptions> planningOptions) : ICodeGenerationPlanningService
{
    private const string ProducedBy = "fuwen-staged-planning";

    public async Task<CodeGenerationPlanningOutcome> PlanAsync(
        string request,
        string model,
        int maxTokens = 12000,
        string? previousFailure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _ = gapBuilder;
        _ = maxTokens;
        _ = previousFailure;
        var tokens = planningOptions.Value;
        var factory = new PlanningStageAdapterFactory(
            llmRouter, domainBuilder, topologyBuilder, contractBuilder, componentBuilder,
            repairer, promptLoader, model, tokens);
        var executors = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var definitions = BuiltInPlanningStages.Catalogue();

        var root = Path.Combine(Path.GetTempPath(), "guyabano-fuwen-service", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalog = new PlanningArtifactCatalog(new FileSystemArtifactRepository(root));
        var runner = new PlanningStageRunner(definitions, executors, catalog);
        var workflowId = Guid.NewGuid().ToString("N");
        try
        {
            var phase1 = StagedPlan(
                [
                    Stage(BuiltInPlanningStages.DomainDiscoveryId, "domain-discovery/main", []),
                    Stage(
                        BuiltInPlanningStages.SolutionTopologyId,
                        "solution-topology/main",
                        ["domain-discovery/main"]),
                ],
                "Discover the domain, then shape the solution topology.",
                definitions.Version);
            var run1 = await runner.RunAsync(
                workflowId, phase1, new HashSet<string>(), request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!run1.Succeeded)
            {
                throw new InvalidOperationException(
                    "Staged planning failed: " + string.Join(" ", run1.Diagnostics));
            }

            var domainJson = run1.Outputs["domain-discovery/main"].GetRawText();
            var topologyJson = run1.Outputs["solution-topology/main"].GetRawText();
            var domain = JsonSerializer.Deserialize<DomainDiscovery>(domainJson)
                ?? throw new InvalidOperationException("Domain stage produced an unreadable artifact.");
            var topology = JsonSerializer.Deserialize<SolutionTopology>(topologyJson)
                ?? throw new InvalidOperationException("Topology stage produced an unreadable artifact.");
            var domainRevision = run1.Published.Single(version =>
                version.StartsWith("domain-discovery/", StringComparison.Ordinal));
            var topologyRevision = run1.Published.Single(version =>
                version.StartsWith("solution-topology/", StringComparison.Ordinal));

            var instances = new List<PlannedStage>();
            var seen = new List<string>();
            foreach (var context in TopologicalContexts.Order(topology.BoundedContexts))
            {
                var slug = ArtifactSlugs.Slug(context.Name);
                var contractName = $"contracts/{slug}";
                var componentName = $"components/{slug}";
                instances.Add(Stage(
                    BuiltInPlanningStages.ContractDesignId,
                    contractName,
                    [.. seen],
                    domainRevision,
                    topologyRevision));
                seen.Add(contractName);
                instances.Add(Stage(
                    BuiltInPlanningStages.ComponentDesignId,
                    componentName,
                    [.. seen],
                    domainRevision,
                    topologyRevision));
                seen.Add(componentName);
            }

            var phase2 = StagedPlan(
                instances,
                "Design each bounded context in topological order.",
                definitions.Version,
                [.. run1.Published]);
            var seeds = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["domain-discovery/main"] = JsonDocument.Parse(domainJson).RootElement.Clone(),
                ["solution-topology/main"] = JsonDocument.Parse(topologyJson).RootElement.Clone(),
            };
            var known = new HashSet<string>(run1.Published, StringComparer.Ordinal);
            var run2 = await runner.RunAsync(
                workflowId, phase2, known, request, seeds, cancellationToken).ConfigureAwait(false);
            if (!run2.Succeeded)
            {
                throw new InvalidOperationException(
                    "Staged planning failed: " + string.Join(" ", run2.Diagnostics));
            }

            var catalogs = new List<BoundedContextContractCatalog>();
            var manifests = new List<BoundedContextComponentManifest>();
            foreach (var context in TopologicalContexts.Order(topology.BoundedContexts))
            {
                var slug = ArtifactSlugs.Slug(context.Name);
                catalogs.Add(JsonSerializer.Deserialize<BoundedContextContractCatalog>(
                        run2.Outputs[$"contracts/{slug}"].GetRawText())
                    ?? throw new InvalidOperationException(
                        $"Context design for '{context.Name}' produced an unreadable catalog."));
                manifests.Add(JsonSerializer.Deserialize<BoundedContextComponentManifest>(
                        run2.Outputs[$"components/{slug}"].GetRawText())
                    ?? throw new InvalidOperationException(
                        $"Context design for '{context.Name}' produced an unreadable manifest."));
            }

            var artifacts = new StagedPlanningArtifacts(domain, topology, catalogs, manifests);
            var plan = StagedCodeGenerationPlanAssembler.Assemble(artifacts);
            return new CodeGenerationPlanningOutcome(
                true, PlanningFailure.None, null, model, plan, false, [])
            {
                StagedArtifacts = artifacts,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CodeGenerationPlanningOutcome(
                false, PlanningFailure.InvalidPlan, exception.Message, model, null, false, []);
        }
    }

    private static PlannedStage Stage(
        string stageId,
        string name,
        IReadOnlyList<string> dependsOn,
        params string[] inputArtifacts) => new()
        {
            StageId = stageId,
            Name = name,
            DependsOn = dependsOn,
            InputArtifacts = inputArtifacts,
        };

    private static PlanningStagePlan StagedPlan(
        IReadOnlyList<PlannedStage> stages,
        string rationale,
        string catalogueVersion,
        IReadOnlyList<string>? inputRevisions = null) => new()
        {
            Stages = stages,
            Rationale = rationale,
            Provenance = new PlanningStagePlanProvenance
            {
                ProducedBy = ProducedBy,
                InputRevisions = inputRevisions ?? [],
                DefinitionCatalogueVersion = catalogueVersion,
            },
        };
}
