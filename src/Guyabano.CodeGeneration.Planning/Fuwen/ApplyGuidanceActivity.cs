using System.Text.Json;
using System.Text.Json.Nodes;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Pure Guyabano activity folding gap guidance back into a joint attempt
/// envelope: when guidance is non-empty it replaces the envelope's error
/// (feeding the next attempt's <c>previousFailure</c>); otherwise the
/// envelope passes through unchanged.
/// </summary>
public sealed class ApplyGuidanceActivity : IActivityExecutor
{
    public ValueTask<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attempt = PlanningStageArguments.ReadActivityJson(request, "attempt");
        var guidance = PlanningStageArguments.ReadActivityString(request, "guidance");
        var node = JsonNode.Parse(attempt.GetRawText())!.AsObject();
        if (!string.IsNullOrEmpty(guidance))
            node["error"] = guidance;
        return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
            RuntimeValue.FromJson(JsonDocument.Parse(node.ToJsonString()).RootElement)));
    }
}
