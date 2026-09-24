using System.Text.Json;
using Penghou.Guihua.Baize;
using Penghou.Guihua;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Fuwen;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Fuwen inference executor running Guyabano's real per-context contract
/// design stage for one fan-out item. The item bundle carries the context,
/// domain, and topology artifacts; upstream catalogs are empty (independent
/// contexts — dependent chains need sequential handling, see slice 4).
/// Validation mirrors the service's per-catalog checks.
/// </summary>
public sealed class PlanningContractExecutor(
    ILlmRouter llmRouter,
    IPromptBuilder<ContractDesignPromptContext> promptBuilder,
    ILlmStructuredOutputRepairer repairer,
    string model,
    int maxTokens = 12000,
    bool outputEnvelope = false) : IInferenceExecutor, IInferenceExecutorPreflight
{
    public async ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var previousFailure = PlanningStageArguments.ReadString(request, "previousFailure", required: false);
        var bundle = PlanningStageArguments.ReadJson(request, "bundle", required: true)!.Value;
        var context = JsonSerializer.Deserialize<BoundedContextPlan>(
            bundle.GetProperty("context").GetRawText())
            ?? throw new InvalidOperationException("Contract inference received an unreadable bounded context.");
        var domain = JsonSerializer.Deserialize<DomainDiscovery>(
            bundle.GetProperty("domain").GetRawText())
            ?? throw new InvalidOperationException("Contract inference received an unreadable domain artifact.");
        var topology = JsonSerializer.Deserialize<SolutionTopology>(
            bundle.GetProperty("topology").GetRawText())
            ?? throw new InvalidOperationException("Contract inference received an unreadable topology artifact.");
        var upstream = ReadCatalogs(request);
        var format = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<BoundedContextContractCatalog>());
        var llmRequest = await promptBuilder.BuildAsync(
            new ContractDesignPromptContext(domain, topology, context, upstream, format, maxTokens, previousFailure),
            cancellationToken).ConfigureAwait(false);
        var response = await llmRouter.CompleteStreamingAsync(
            model, llmRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response is null)
            return Failed(context.Name,
                $"Contract design for '{context.Name}' returned no response.");
        var repaired = await repairer.RepairAsync(
            response, format, cancellationToken).ConfigureAwait(false);
        var parsed = StructuredPlanningStageParser<BoundedContextContractCatalog>.Parse(repaired);
        if (!parsed.Succeeded || parsed.Value is null)
            return Failed(context.Name,
                $"Contract design for '{context.Name}' returned invalid structured output: {parsed.Error ?? "Parsing failed."}");
        if (!parsed.Value.BoundedContextName.Equals(context.Name, StringComparison.Ordinal))
            return Failed(context.Name,
                $"Contract catalog must target bounded context '{context.Name}', not '{parsed.Value.BoundedContextName}'.");
        var errors = StagedPlanningValidator.ValidateContracts(domain, topology, [parsed.Value, .. upstream]);
        if (errors.Count > 0)
            return Failed(context.Name,
                $"Contract design for '{context.Name}' failed validation: {string.Join(" ", errors)}");
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
            artifact = (BoundedContextContractCatalog?)null,
            error,
        }));
        return InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(envelope.RootElement));
    }

    public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement) =>
        PlanningInferencePreflight.Check(requirement);

    private static IReadOnlyList<BoundedContextContractCatalog> ReadCatalogs(
        InferenceExecutionRequest request)
    {
        // Optional "upstreamCatalogs" list argument (R25-omittable), falling
        // back to the bundle's embedded upstream list: phased execution
        // embeds prior-phase artifacts in bundle literals because closed
        // regions forbid runtime upstream references inside retry bodies.
        foreach (var argument in request.Arguments)
        {
            if (argument.Value is not JsonRuntimeValue json)
                continue;
            if (string.Equals(argument.Name, "upstreamCatalogs", StringComparison.Ordinal) &&
                json.Value.ValueKind == JsonValueKind.Array)
            {
                return ReadCatalogArray(json.Value);
            }
            if (string.Equals(argument.Name, "bundle", StringComparison.Ordinal) &&
                json.Value.ValueKind == JsonValueKind.Object &&
                json.Value.TryGetProperty("upstreamCatalogs", out var embedded) &&
                embedded.ValueKind == JsonValueKind.Array)
            {
                return ReadCatalogArray(embedded);
            }
        }
        return [];
    }

    private static IReadOnlyList<BoundedContextContractCatalog> ReadCatalogArray(JsonElement array) =>
        array.EnumerateArray()
            .Select(element => JsonSerializer.Deserialize<BoundedContextContractCatalog>(element.GetRawText())
                ?? throw new InvalidOperationException("Contract inference received an unreadable upstream catalog."))
            .ToArray();
}
