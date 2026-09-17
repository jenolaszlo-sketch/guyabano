using System.Text.Json;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Fuwen;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Fuwen inference executor running Guyabano's real per-context component
/// design stage for one fan-out item, consuming the sibling contract
/// catalog produced earlier in the same item body. Upstream manifests are
/// empty (independent contexts — dependent chains need sequential
/// handling, see slice 4). Validation mirrors the service's per-manifest
/// checks.
/// </summary>
public sealed class PlanningComponentExecutor(
    ILlmRouter llmRouter,
    IPromptBuilder<ComponentDesignPromptContext> promptBuilder,
    ILlmStructuredOutputRepairer repairer,
    string model,
    int maxTokens = 16000,
    bool outputEnvelope = false) : IInferenceExecutor
{
    public async ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var previousFailure = PlanningStageArguments.ReadString(request, "previousFailure", required: false);
        var bundle = PlanningStageArguments.ReadJson(request, "bundle", required: true)!.Value;
        var catalogJson = PlanningStageArguments.ReadJson(request, "catalog", required: true)!.Value;
        var context = JsonSerializer.Deserialize<BoundedContextPlan>(
            bundle.GetProperty("context").GetRawText())
            ?? throw new InvalidOperationException("Component inference received an unreadable bounded context.");
        var domain = JsonSerializer.Deserialize<DomainDiscovery>(
            bundle.GetProperty("domain").GetRawText())
            ?? throw new InvalidOperationException("Component inference received an unreadable domain artifact.");
        var topology = JsonSerializer.Deserialize<SolutionTopology>(
            bundle.GetProperty("topology").GetRawText())
            ?? throw new InvalidOperationException("Component inference received an unreadable topology artifact.");
        var catalog = JsonSerializer.Deserialize<BoundedContextContractCatalog>(catalogJson.GetRawText())
            ?? throw new InvalidOperationException("Component inference received an unreadable contract catalog.");
        var upstreamCatalogs = ReadCatalogs(request);
        var upstreamManifests = ReadManifests(request);
        var format = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<BoundedContextComponentManifest>());
        var llmRequest = await promptBuilder.BuildAsync(
            new ComponentDesignPromptContext(domain, topology, context, [catalog, .. upstreamCatalogs], upstreamManifests, format, maxTokens, previousFailure),
            cancellationToken).ConfigureAwait(false);
        var response = await llmRouter.CompleteStreamingAsync(
            model, llmRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response is null)
            return Failed(context.Name,
                $"Component design for '{context.Name}' returned no response.");
        var repaired = await repairer.RepairAsync(
            response, format, cancellationToken).ConfigureAwait(false);
        var parsed = StructuredPlanningStageParser<BoundedContextComponentManifest>.Parse(repaired);
        if (!parsed.Succeeded || parsed.Value is null)
            return Failed(context.Name,
                $"Component design for '{context.Name}' returned invalid structured output: {parsed.Error ?? "Parsing failed."}");
        if (!parsed.Value.BoundedContextName.Equals(context.Name, StringComparison.Ordinal))
            return Failed(context.Name,
                $"Component manifest must target bounded context '{context.Name}', not '{parsed.Value.BoundedContextName}'.");
        var errors = StagedPlanningValidator.ValidateComponents(domain, topology, [catalog, .. upstreamCatalogs], [parsed.Value, .. upstreamManifests]);
        if (errors.Count > 0)
            return Failed(context.Name,
                $"Component design for '{context.Name}' failed validation: {string.Join(" ", errors)}");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(parsed.Value));
        if (outputEnvelope)
        {
            using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                ok = true,
                artifact = parsed.Value,
                error = (string?)null,
            }));
            return InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(envelope.RootElement));
        }
        return InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(document.RootElement));
    }

    private InferenceExecutionResult Failed(string contextName, string error)
    {
        if (!outputEnvelope)
            throw new InvalidOperationException(error);
        using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = false,
            artifact = (BoundedContextComponentManifest?)null,
            error,
        }));
        return InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(envelope.RootElement));
    }

    private static IReadOnlyList<BoundedContextContractCatalog> ReadCatalogs(
        InferenceExecutionRequest request)
    {
        foreach (var argument in request.Arguments)
        {
            if (string.Equals(argument.Name, "upstreamCatalogs", StringComparison.Ordinal) &&
                argument.Value is JsonRuntimeValue json &&
                json.Value.ValueKind == JsonValueKind.Array)
            {
                return json.Value.EnumerateArray()
                    .Select(element => JsonSerializer.Deserialize<BoundedContextContractCatalog>(element.GetRawText())
                        ?? throw new InvalidOperationException("Component inference received an unreadable upstream catalog."))
                    .ToArray();
            }
        }
        return [];
    }

    private static IReadOnlyList<BoundedContextComponentManifest> ReadManifests(
        InferenceExecutionRequest request)
    {
        foreach (var argument in request.Arguments)
        {
            if (string.Equals(argument.Name, "upstreamManifests", StringComparison.Ordinal) &&
                argument.Value is JsonRuntimeValue json &&
                json.Value.ValueKind == JsonValueKind.Array)
            {
                return json.Value.EnumerateArray()
                    .Select(element => JsonSerializer.Deserialize<BoundedContextComponentManifest>(element.GetRawText())
                        ?? throw new InvalidOperationException("Component inference received an unreadable upstream manifest."))
                    .ToArray();
            }
        }
        return [];
    }
}
