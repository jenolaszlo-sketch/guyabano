using System.Text.Json;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Fuwen;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Guyabano activity running one focused gap-resolution attempt, mirroring
/// <c>CodeGenerationPlanningService.ResolveGapAsync</c>: the real
/// <c>planning-gap-resolution</c> pack, repair, parse, and field checks over
/// the failing stage artifact. Emits correction guidance text, or throws
/// when user input is required (mirroring the service failure).
/// </summary>
/// <remarks>
/// Takes the joint attempt envelope plus the original request and skips
/// (empty guidance, no LLM call) when the attempt already succeeded. The
/// skip lives here rather than in a plan conditional because closed
/// regions forbid branch bodies from reading iteration outputs; the skip
/// is observable as zero router calls on success paths.
/// </remarks>
public sealed class ResolveStageGuidanceActivity(
    ILlmRouter llmRouter,
    IPromptBuilder<PlanningGapResolutionPromptContext> promptBuilder,
    ILlmStructuredOutputRepairer repairer,
    string model,
    int maxTokens = 6000) : IActivityExecutor
{
    public async ValueTask<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var originalRequest = PlanningStageArguments.ReadActivityString(request, "request");
        var attempt = PlanningStageArguments.ReadActivityJson(request, "attempt");
        if (attempt.ValueKind == JsonValueKind.Object &&
            attempt.TryGetProperty("ok", out var ok) &&
            ok.ValueKind == JsonValueKind.True)
        {
            using var empty = JsonDocument.Parse("\"\"");
            return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(empty.RootElement));
        }
        var stage = attempt.TryGetProperty("retryStage", out var stageElement) &&
            stageElement.ValueKind == JsonValueKind.String
            ? stageElement.GetString()!
            : throw new InvalidOperationException("Gap resolution requires a retry stage.");
        var artifact = attempt.TryGetProperty("retryArtifact", out var artifactElement)
            ? artifactElement.Clone()
            : throw new InvalidOperationException("Gap resolution requires a retry artifact.");
        var issue = attempt.TryGetProperty("error", out var issueElement) &&
            issueElement.ValueKind == JsonValueKind.String
            ? issueElement.GetString()!
            : throw new InvalidOperationException("Gap resolution requires an issue.");
        var format = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<PlanningGapResolution>());
        var llmRequest = await promptBuilder.BuildAsync(
            new PlanningGapResolutionPromptContext(
                originalRequest, stage, artifact.GetRawText(), issue, format, maxTokens),
            cancellationToken).ConfigureAwait(false);
        var response = await llmRouter.CompleteStreamingAsync(
            model, llmRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response is null)
            throw new InvalidOperationException(
                $"The focused resolver for {stage} returned no response.");
        var repaired = await repairer.RepairAsync(
            response, format, cancellationToken).ConfigureAwait(false);
        var parsed = StructuredPlanningStageParser<PlanningGapResolution>.Parse(repaired);
        if (!parsed.Succeeded || parsed.Value is null ||
            string.IsNullOrWhiteSpace(parsed.Value.Decision) ||
            parsed.Value.Reasons.Count == 0 ||
            parsed.Value.Consequences.Count == 0 ||
            (parsed.Value.RequiresUserInput &&
             string.IsNullOrWhiteSpace(parsed.Value.UserQuestion)))
        {
            throw new InvalidOperationException(
                parsed.Error ??
                "A focused resolution must contain a decision, reasons, consequences, and a question when user input is required.");
        }
        if (parsed.Value.RequiresUserInput)
            throw new InvalidOperationException(parsed.Value.UserQuestion);
        var guidance =
            $"The stage artifact was rejected: {issue} " +
            $"A focused resolver selected this correction: {parsed.Value.Decision} " +
            $"Reasons: {string.Join("; ", parsed.Value.Reasons)}. " +
            $"Consequences: {string.Join("; ", parsed.Value.Consequences)}. " +
            "Apply this correction exactly, preserve all valid artifact content, and return a complete corrected stage artifact.";
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(guidance));
        return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(document.RootElement));
    }
}
