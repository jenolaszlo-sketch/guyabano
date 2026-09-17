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
/// <c>CodeGenerationPlanningService</c>.
/// </summary>
/// <remarks>
/// In strict mode (default) a failed attempt throws, failing the step.
/// In envelope mode (<paramref name="outputEnvelope"/>), a failed attempt
/// returns <c>{"ok":false,"error":...}</c> instead, so a Fuwen repeat loop
/// can implement the service's bounded stage-retry loop: loop state carries
/// the envelope, <c>previousFailure</c> binds the envelope's error back
/// into the next attempt, and the break condition checks the envelope's
/// <c>ok</c> flag.
/// </remarks>
public sealed class PlanningDomainDiscoveryExecutor(
    ILlmRouter llmRouter,
    IPromptBuilder<DomainDiscoveryPromptContext> promptBuilder,
    ILlmStructuredOutputRepairer repairer,
    string model,
    int maxTokens = 8000,
    bool outputEnvelope = false) : IInferenceExecutor
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
        if (response is null)
            return Failed("The domain discovery stage returned no response.");
        var repaired = await repairer.RepairAsync(
            response, format, cancellationToken).ConfigureAwait(false);
        var parsed = StructuredPlanningStageParser<DomainDiscovery>.Parse(repaired);
        if (!parsed.Succeeded || parsed.Value is null)
            return Failed(
                $"Domain discovery stage returned invalid structured output: {parsed.Error ?? "Parsing failed."}");
        var errors = StagedPlanningValidator.ValidateDomain(parsed.Value);
        if (errors.Count > 0)
            return Failed(
                $"Domain discovery stage failed validation: {string.Join(" ", errors)}");
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

    private InferenceExecutionResult Failed(string error)
    {
        if (!outputEnvelope)
            throw new InvalidOperationException(error);
        using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = false,
            artifact = (DomainDiscovery?)null,
            error,
        }));
        return InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(envelope.RootElement));
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
