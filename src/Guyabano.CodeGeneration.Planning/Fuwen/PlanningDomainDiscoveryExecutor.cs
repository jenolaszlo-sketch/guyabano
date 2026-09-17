using System.Text.Json;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Fuwen;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Fuwen inference executor running Guyabano's real domain-discovery
/// planning stage: the real <c>domain-discovery</c> Scriban prompt pack,
/// the configured planner model, the real <see cref="DomainDiscovery"/>
/// JSON schema, and the real repair/parse/validate chain. It mirrors one
/// <c>ExecuteStageAsync&lt;DomainDiscovery&gt;</c> attempt from
/// <c>CodeGenerationPlanningService</c> (single attempt; stage retries map
/// to Fuwen repeat loops, not to this executor).
/// </summary>
public sealed class PlanningDomainDiscoveryExecutor(
    ILlmRouter llmRouter,
    IPromptBuilder<DomainDiscoveryPromptContext> promptBuilder,
    ILlmStructuredOutputRepairer repairer,
    string model,
    int maxTokens = 8000) : IInferenceExecutor
{
    public async ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestText = ReadArgument(request, "request", required: true)!;
        var previousFailure = ReadArgument(request, "previousFailure", required: false);
        var format = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<DomainDiscovery>());
        var llmRequest = await promptBuilder.BuildAsync(
            new DomainDiscoveryPromptContext(requestText, format, maxTokens, previousFailure),
            cancellationToken).ConfigureAwait(false);
        var response = await llmRouter.CompleteStreamingAsync(
            model, llmRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
        var repaired = await repairer.RepairAsync(
            response, format, cancellationToken).ConfigureAwait(false);
        var parsed = StructuredPlanningStageParser<DomainDiscovery>.Parse(repaired);
        if (!parsed.Succeeded || parsed.Value is null)
            throw new InvalidOperationException(
                $"Domain discovery stage returned invalid structured output: {parsed.Error ?? "Parsing failed."}");
        var errors = StagedPlanningValidator.ValidateDomain(parsed.Value);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Domain discovery stage failed validation: {string.Join(" ", errors)}");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(parsed.Value));
        return InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(document.RootElement));
    }

    private static string? ReadArgument(
        InferenceExecutionRequest request, string name, bool required)
    {
        foreach (var argument in request.Arguments)
        {
            if (string.Equals(argument.Name, name, StringComparison.Ordinal) &&
                argument.Value is JsonRuntimeValue json &&
                json.Value.ValueKind == JsonValueKind.String)
            {
                return json.Value.GetString();
            }
        }
        if (required)
            throw new InvalidOperationException(
                $"Domain discovery inference requires a string '{name}' argument.");
        return null;
    }
}
