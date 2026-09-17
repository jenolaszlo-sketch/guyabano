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
    int maxTokens = 16000) : IInferenceExecutor
{
    public async ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
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
        var format = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<BoundedContextComponentManifest>());
        var llmRequest = await promptBuilder.BuildAsync(
            new ComponentDesignPromptContext(domain, topology, context, [catalog], [], format, maxTokens),
            cancellationToken).ConfigureAwait(false);
        var response = await llmRouter.CompleteStreamingAsync(
            model, llmRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response is null)
            throw new InvalidOperationException(
                $"Component design for '{context.Name}' returned no response.");
        var repaired = await repairer.RepairAsync(
            response, format, cancellationToken).ConfigureAwait(false);
        var parsed = StructuredPlanningStageParser<BoundedContextComponentManifest>.Parse(repaired);
        if (!parsed.Succeeded || parsed.Value is null)
            throw new InvalidOperationException(
                $"Component design for '{context.Name}' returned invalid structured output: {parsed.Error ?? "Parsing failed."}");
        if (!parsed.Value.BoundedContextName.Equals(context.Name, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Component manifest must target bounded context '{context.Name}', not '{parsed.Value.BoundedContextName}'.");
        var errors = StagedPlanningValidator.ValidateComponents(domain, topology, [catalog], [parsed.Value]);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Component design for '{context.Name}' failed validation: {string.Join(" ", errors)}");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(parsed.Value));
        return InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(document.RootElement));
    }
}
