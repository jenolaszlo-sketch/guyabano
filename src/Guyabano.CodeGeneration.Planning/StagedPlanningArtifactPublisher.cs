using System.Text;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Publishes a <see cref="StagedPlanningArtifacts"/> result into the revisioned
/// planning artifact catalog, assigning stable identities and declaring the
/// upstream versions each artifact was derived from so later impact analysis
/// can invalidate only the affected planning region.
/// </summary>
public sealed class StagedPlanningArtifactPublisher(IPlanningArtifactCatalog catalog)
{
    public const string DomainKind = "domain-discovery";
    public const string TopologyKind = "solution-topology";
    public const string ContractKind = "contracts";
    public const string ComponentKind = "components";

    public const string PlanDomain = "plan-domain";
    public const string PlanTopology = "plan-topology";
    public const string PlanContracts = "plan-contracts";
    public const string PlanComponents = "plan-components";

    private const int SchemaVersion = 1;

    public async Task<StagedPlanningArtifactVersions> PublishAsync(
        string workflowId,
        StagedPlanningArtifacts artifacts,
        PlanningArtifactState state = PlanningArtifactState.Valid,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(artifacts);

        var domain = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<DomainDiscovery>(
                workflowId,
                new PlanningArtifactKey(DomainKind, "main"),
                SchemaVersion,
                PlanDomain,
                artifacts.Domain,
                Inputs: [],
                State: state)
            {
                SessionId = sessionId,
            },
            cancellationToken).ConfigureAwait(false);

        var topology = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<SolutionTopology>(
                workflowId,
                new PlanningArtifactKey(TopologyKind, "main"),
                SchemaVersion,
                PlanTopology,
                artifacts.Topology,
                Inputs: [domain.Version],
                State: state)
            {
                SessionId = sessionId,
            },
            cancellationToken).ConfigureAwait(false);

        var contexts = artifacts.Topology.BoundedContexts
            .ToDictionary(context => context.Name, StringComparer.Ordinal);

        // Contract catalogs are produced in topological order; each declares the
        // topology plus the contracts of the contexts it depends on.
        var contracts = new Dictionary<string, PlanningArtifactVersion>(StringComparer.Ordinal);
        foreach (var contractCatalog in artifacts.ContractCatalogs)
        {
            var inputs = new List<PlanningArtifactVersion> { topology.Version };
            if (contexts.TryGetValue(contractCatalog.BoundedContextName, out var context))
            {
                inputs.AddRange(context.DependsOnContextNames
                    .Where(contracts.ContainsKey)
                    .Select(name => contracts[name]));
            }

            var published = await catalog.PublishAsync(
                new PublishPlanningArtifactRequest<BoundedContextContractCatalog>(
                    workflowId,
                    new PlanningArtifactKey(ContractKind, Slug(contractCatalog.BoundedContextName)),
                    SchemaVersion,
                    PlanContracts,
                    contractCatalog,
                    Inputs: inputs,
                    State: state)
                {
                    SessionId = sessionId,
                },
                cancellationToken).ConfigureAwait(false);
            contracts[contractCatalog.BoundedContextName] = published.Version;
        }

        var components = new Dictionary<string, PlanningArtifactVersion>(StringComparer.Ordinal);
        foreach (var manifest in artifacts.ComponentManifests)
        {
            var inputs = new List<PlanningArtifactVersion>();
            if (contracts.TryGetValue(manifest.BoundedContextName, out var contract))
                inputs.Add(contract);

            var published = await catalog.PublishAsync(
                new PublishPlanningArtifactRequest<BoundedContextComponentManifest>(
                    workflowId,
                    new PlanningArtifactKey(ComponentKind, Slug(manifest.BoundedContextName)),
                    SchemaVersion,
                    PlanComponents,
                    manifest,
                    Inputs: inputs,
                    State: state)
                {
                    SessionId = sessionId,
                },
                cancellationToken).ConfigureAwait(false);
            components[manifest.BoundedContextName] = published.Version;
        }

        return new StagedPlanningArtifactVersions(
            domain.Version,
            topology.Version,
            contracts,
            components);
    }

    /// <summary>
    /// Publishes the execution design as the next two links in the planning
    /// cascade: the semantic execution graph, then its bindings. The graph
    /// declares the passed design inputs; the bindings declare the graph.
    /// </summary>
    public async Task<PlannedExecutionDesignVersions> PublishExecutionDesignAsync(
        string workflowId,
        PlannedExecutionDesign design,
        IReadOnlyList<PlanningArtifactVersion> designInputs,
        PlanningArtifactState state = PlanningArtifactState.Valid,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(designInputs);

        var graph = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlannedExecutionGraph>(
                workflowId,
                new PlanningArtifactKey("execution-graph", "main"),
                SchemaVersion,
                "build-execution-graph",
                design.Graph,
                Inputs: designInputs,
                State: state)
            {
                SessionId = sessionId,
            },
            cancellationToken).ConfigureAwait(false);

        var bindings = await catalog.PublishAsync(
            new PublishPlanningArtifactRequest<PlannedExecutionBindings>(
                workflowId,
                new PlanningArtifactKey("bindings", "main"),
                SchemaVersion,
                "resolve-bindings",
                design.Bindings,
                Inputs: [graph.Version],
                State: state)
            {
                SessionId = sessionId,
            },
            cancellationToken).ConfigureAwait(false);

        return new PlannedExecutionDesignVersions(graph.Version, bindings.Version);
    }

    /// <summary>Builds the stable catalog key for one bounded-context artifact.</summary>
    public static PlanningArtifactKey ContextKey(string kind, string contextName) =>
        new(kind, Slug(contextName));

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "unnamed" : slug;
    }
}
