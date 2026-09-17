using System.Text.Json;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Fuwen;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Fuwen inference executor running Guyabano's real solution-topology
/// planning stage (single attempt mirroring the service's topology call
/// with no carried architecture failure).
/// </summary>
public sealed class PlanningTopologyExecutor(
    ILlmRouter llmRouter,
    IPromptBuilder<SolutionTopologyPromptContext> promptBuilder,
    ILlmStructuredOutputRepairer repairer,
    string model,
    int maxTokens = 10000) : IInferenceExecutor
{
    public async ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestText = PlanningStageArguments.ReadString(request, "request", required: true)!;
        var domainJson = PlanningStageArguments.ReadJson(request, "domain", required: true)!;
        var domain = JsonSerializer.Deserialize<DomainDiscovery>(domainJson.Value.GetRawText())
            ?? throw new InvalidOperationException("Topology inference received an unreadable domain artifact.");
        var format = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<SolutionTopology>());
        var llmRequest = await promptBuilder.BuildAsync(
            new SolutionTopologyPromptContext(requestText, domain, format, maxTokens),
            cancellationToken).ConfigureAwait(false);
        var response = await llmRouter.CompleteStreamingAsync(
            model, llmRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response is null)
            throw new InvalidOperationException("The solution topology stage returned no response.");
        var repaired = await repairer.RepairAsync(
            response, format, cancellationToken).ConfigureAwait(false);
        var parsed = StructuredPlanningStageParser<SolutionTopology>.Parse(repaired);
        if (!parsed.Succeeded || parsed.Value is null)
            throw new InvalidOperationException(
                $"Solution topology stage returned invalid structured output: {parsed.Error ?? "Parsing failed."}");
        var errors = StagedPlanningValidator.ValidateTopology(domain, parsed.Value);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Solution topology stage failed validation: {string.Join(" ", errors)}");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(parsed.Value));
        return InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(document.RootElement));
    }
}
