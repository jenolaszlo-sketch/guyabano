using System.Text.Json;
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
    int maxTokens = 12000) : IInferenceExecutor
{
    public async ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
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
        var format = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<BoundedContextContractCatalog>());
        var llmRequest = await promptBuilder.BuildAsync(
            new ContractDesignPromptContext(domain, topology, context, [], format, maxTokens),
            cancellationToken).ConfigureAwait(false);
        var response = await llmRouter.CompleteStreamingAsync(
            model, llmRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response is null)
            throw new InvalidOperationException(
                $"Contract design for '{context.Name}' returned no response.");
        var repaired = await repairer.RepairAsync(
            response, format, cancellationToken).ConfigureAwait(false);
        var parsed = StructuredPlanningStageParser<BoundedContextContractCatalog>.Parse(repaired);
        if (!parsed.Succeeded || parsed.Value is null)
            throw new InvalidOperationException(
                $"Contract design for '{context.Name}' returned invalid structured output: {parsed.Error ?? "Parsing failed."}");
        if (!parsed.Value.BoundedContextName.Equals(context.Name, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Contract catalog must target bounded context '{context.Name}', not '{parsed.Value.BoundedContextName}'.");
        var errors = StagedPlanningValidator.ValidateContracts(domain, topology, [parsed.Value]);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Contract design for '{context.Name}' failed validation: {string.Join(" ", errors)}");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(parsed.Value));
        return InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(document.RootElement));
    }
}
