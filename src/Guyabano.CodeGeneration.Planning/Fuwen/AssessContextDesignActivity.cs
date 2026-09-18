using System.Text.Json;
using System.Text.Json.Nodes;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Pure Guyabano activity aggregating one context-design attempt from the
/// contract and component stage envelopes into the joint attempt envelope
/// <c>{ok, catalog, manifest, error, retryStage, retryArtifact}</c>.
/// Stage-level validation already ran inside the executors; this node
/// selects the overall outcome and the failing side for gap resolution.
/// Retry loops continue from this envelope and break on its <c>ok</c> flag.
/// </summary>
public sealed class AssessContextDesignActivity : IActivityExecutor
{
    public ValueTask<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var contractEnvelope = PlanningStageArguments.ReadActivityJson(request, "contract");
        var manifestEnvelope = PlanningStageArguments.ReadActivityJson(request, "manifest");
        JsonNode? catalog = null;
        JsonNode? manifest = null;
        string? error = null;
        string? retryStage = null;
        JsonNode? retryArtifact = null;
        if (!contractEnvelope.GetProperty("ok").GetBoolean())
        {
            error = contractEnvelope.GetProperty("error").GetString();
            retryStage = "contract-design";
        }
        else
        {
            catalog = JsonNode.Parse(contractEnvelope.GetProperty("artifact").GetRawText());
            if (!manifestEnvelope.GetProperty("ok").GetBoolean())
            {
                error = manifestEnvelope.GetProperty("error").GetString();
                retryStage = "component-design";
                retryArtifact = catalog;
            }
            else
            {
                manifest = JsonNode.Parse(manifestEnvelope.GetProperty("artifact").GetRawText());
            }
        }

        var envelope = new JsonObject
        {
            ["ok"] = error is null,
            ["catalog"] = catalog,
            ["manifest"] = manifest,
            ["error"] = error,
            ["retryStage"] = retryStage,
            ["retryArtifact"] = retryArtifact,
        };
        return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
            RuntimeValue.FromJson(JsonDocument.Parse(envelope.ToJsonString()).RootElement)));
    }
}
