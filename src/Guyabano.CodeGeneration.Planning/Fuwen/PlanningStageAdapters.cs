using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Fuwen;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Bridges the generic stage runner to the real Fuwen planning executors:
/// binds upstream outputs to node arguments, synthesizes the inference
/// request, retries through <c>previousFailure</c> up to the definition's
/// budget, and extracts the artifact JSON. Binding errors fail immediately;
/// model failures retry.
/// </summary>
public abstract class PlanningStageAdapterBase(
    IInferenceExecutor inner,
    DescriptorReference profile,
    DescriptorReference template) : IPlanningStageExecutor
{
    public abstract string StageId { get; }

    /// <summary>Node arguments (without <c>previousFailure</c>) for one attempt.</summary>
    protected abstract IReadOnlyDictionary<string, JsonElement> BuildArguments(
        StageExecutionInput input);

    public async Task<StageExecutionResult> ExecuteAsync(
        StageExecutionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyDictionary<string, JsonElement> arguments;
        try
        {
            arguments = BuildArguments(input);
        }
        catch (InvalidOperationException exception)
        {
            return new StageExecutionResult(false, null, [exception.Message]);
        }

        string? failure = null;
        var diagnostics = new List<string>();
        for (var attempt = 1; attempt <= Math.Max(1, input.Definition.MaxAttempts); attempt++)
        {
            var request = BuildRequest(input, arguments, failure, attempt);
            InferenceExecutionResult result;
            try
            {
                result = await inner.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                failure = exception.Message;
                diagnostics.Add($"Attempt {attempt}: {failure}");
                continue;
            }

            if (result.Failure is not null || result.Output is null)
            {
                failure = result.Failure?.Message ?? "The stage returned no output.";
                diagnostics.Add($"Attempt {attempt}: {failure}");
                continue;
            }

            return new StageExecutionResult(
                true, RuntimeValueJson.ToJsonElement(result.Output), []);
        }

        return new StageExecutionResult(false, null, diagnostics);
    }

    private InferenceExecutionRequest BuildRequest(
        StageExecutionInput input,
        IReadOnlyDictionary<string, JsonElement> arguments,
        string? previousFailure,
        int attempt)
    {
        var runtime = arguments
            .Select(pair => new RuntimeArgument(
                pair.Key, RuntimeValue.FromJson(pair.Value)))
            .ToList();
        if (previousFailure is not null)
        {
            runtime.Add(new RuntimeArgument(
                "previousFailure",
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement(previousFailure))));
        }

        return new InferenceExecutionRequest(
            new ExecutionInvocation(
                Fingerprint($"exec:{StageId}"),
                $"adapter/{StageId}",
                $"adapter/{StageId}",
                attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Fingerprint($"req:{StageId}:{attempt}")),
            profile,
            template,
            runtime,
            [],
            new PrimitiveType(FuwenPrimitiveKind.Json));
    }

    private static string Fingerprint(string text) =>
        $"sha256:{FuwenContracts.ExecutionFingerprintVersionV1}:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Exactly one upstream output of the given artifact kind.</summary>
    protected static KeyValuePair<string, JsonElement> SingleUpstreamByKind(
        StageExecutionInput input, string kind)
    {
        var matches = input.UpstreamOutputs
            .Where(pair => KindOf(pair.Key).Equals(kind, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Stage '{input.Stage.Name}' requires exactly one '{kind}' input " +
                $"but found {matches.Length}.");
        }

        return matches[0];
    }

    protected static string KindOf(string identity)
    {
        var slash = identity.IndexOf('/');
        if (slash <= 0)
        {
            throw new InvalidOperationException(
                $"Upstream reference '{identity}' is not a kind/name identity.");
        }

        return identity[..slash];
    }

    protected static string Slug(string value)
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

    protected static JsonElement Element(string value) =>
        JsonSerializer.SerializeToElement(value);
}

/// <summary>Domain discovery from the run goal.</summary>
public sealed class DomainDiscoveryStageAdapter(
    IInferenceExecutor inner,
    DescriptorReference profile,
    DescriptorReference template)
    : PlanningStageAdapterBase(inner, profile, template)
{
    public override string StageId => BuiltInPlanningStages.DomainDiscoveryId;

    protected override IReadOnlyDictionary<string, JsonElement> BuildArguments(
        StageExecutionInput input) => new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["request"] = Element(input.Request),
        };
}

/// <summary>Solution topology from the goal plus the domain output.</summary>
public sealed class SolutionTopologyStageAdapter(
    IInferenceExecutor inner,
    DescriptorReference profile,
    DescriptorReference template)
    : PlanningStageAdapterBase(inner, profile, template)
{
    public override string StageId => BuiltInPlanningStages.SolutionTopologyId;

    protected override IReadOnlyDictionary<string, JsonElement> BuildArguments(
        StageExecutionInput input)
    {
        var domain = SingleUpstreamByKind(input, StagedPlanningArtifactPublisher.DomainKind);
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["request"] = Element(input.Request),
            ["domain"] = domain.Value.Clone(),
        };
    }
}

/// <summary>Shared bundle composition for the per-context design stages.</summary>
public abstract class ContextDesignStageAdapterBase(
    IInferenceExecutor inner,
    DescriptorReference profile,
    DescriptorReference template)
    : PlanningStageAdapterBase(inner, profile, template)
{
    /// <summary>
    /// Composes the context bundle the hand-wired pipeline builds per
    /// bounded context: the matched context plan, domain, topology, and all
    /// upstream catalogs and manifests.
    /// </summary>
    protected static JsonElement ComposeBundle(StageExecutionInput input)
    {
        var domain = SingleUpstreamByKind(input, StagedPlanningArtifactPublisher.DomainKind);
        var topology = SingleUpstreamByKind(input, StagedPlanningArtifactPublisher.TopologyKind);
        var slug = input.Stage.Name[(input.Stage.Name.LastIndexOf('/') + 1)..];
        var topologyPlan = JsonSerializer.Deserialize<SolutionTopology>(topology.Value.GetRawText())
            ?? throw new InvalidOperationException("Topology output is unreadable.");
        var context = topologyPlan.BoundedContexts
            .SingleOrDefault(item => Slug(item.Name).Equals(slug, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Topology has no bounded context matching '{input.Stage.Name}'.");
        var catalogs = input.UpstreamOutputs
            .Where(pair => KindOf(pair.Key)
                .Equals(StagedPlanningArtifactPublisher.ContractKind, StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonNode.Parse(pair.Value.GetRawText()))
            .ToArray();
        var manifests = input.UpstreamOutputs
            .Where(pair => KindOf(pair.Key)
                .Equals(StagedPlanningArtifactPublisher.ComponentKind, StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonNode.Parse(pair.Value.GetRawText()))
            .ToArray();
        return JsonSerializer.SerializeToElement(new
        {
            context,
            domain = JsonNode.Parse(domain.Value.GetRawText()),
            topology = JsonNode.Parse(topology.Value.GetRawText()),
            upstreamCatalogs = catalogs,
            upstreamManifests = manifests,
        });
    }
}

/// <summary>Contract catalog for one bounded context from its bundle.</summary>
public sealed class ContractDesignStageAdapter(
    IInferenceExecutor inner,
    DescriptorReference profile,
    DescriptorReference template)
    : ContextDesignStageAdapterBase(inner, profile, template)
{
    public override string StageId => BuiltInPlanningStages.ContractDesignId;

    protected override IReadOnlyDictionary<string, JsonElement> BuildArguments(
        StageExecutionInput input) => new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["bundle"] = ComposeBundle(input),
        };
}

/// <summary>Component manifest for one bounded context from its bundle plus catalog.</summary>
public sealed class ComponentDesignStageAdapter(
    IInferenceExecutor inner,
    DescriptorReference profile,
    DescriptorReference template)
    : ContextDesignStageAdapterBase(inner, profile, template)
{
    public override string StageId => BuiltInPlanningStages.ComponentDesignId;

    protected override IReadOnlyDictionary<string, JsonElement> BuildArguments(
        StageExecutionInput input)
    {
        var slug = input.Stage.Name[(input.Stage.Name.LastIndexOf('/') + 1)..];
        var catalog = input.UpstreamOutputs
            .Where(pair => KindOf(pair.Key)
                    .Equals(StagedPlanningArtifactPublisher.ContractKind, StringComparison.Ordinal) &&
                Slug(pair.Key[(pair.Key.LastIndexOf('/') + 1)..])
                    .Equals(slug, StringComparison.Ordinal))
            .Select(pair => (JsonElement?)pair.Value.Clone())
            .SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"No contract output matches '{input.Stage.Name}'.");
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["bundle"] = ComposeBundle(input),
            ["catalog"] = catalog,
        };
    }
}
