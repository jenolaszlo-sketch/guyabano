using System.Text.Json;
using System.Text.Json.Nodes;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Guyabano activity bundling one fan-out item per bounded context:
/// <c>{context, domain, topology}</c>. Fan-out bodies are closed regions,
/// so per-item stage inputs must travel inside the item itself; this
/// activity assembles them at the parent region from the admitted domain
/// and topology artifacts. Contexts are emitted in topology order with
/// empty upstream scope (independent contexts only).
/// </summary>
public sealed class BundleContractInputsActivity : IActivityExecutor
{
    public ValueTask<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var domainJson = PlanningStageArguments.ReadActivityJson(request, "domain");
        var topologyJson = PlanningStageArguments.ReadActivityJson(request, "topology");
        var domain = JsonSerializer.Deserialize<DomainDiscovery>(domainJson.GetRawText())
            ?? throw new InvalidOperationException("Bundle activity received an unreadable domain artifact.");
        var topology = JsonSerializer.Deserialize<SolutionTopology>(topologyJson.GetRawText())
            ?? throw new InvalidOperationException("Bundle activity received an unreadable topology artifact.");
        var items = new JsonArray();
        foreach (var context in topology.BoundedContexts)
        {
            items.Add(new JsonObject
            {
                ["name"] = context.Name,
                ["bundle"] = new JsonObject
                {
                    ["context"] = JsonNode.Parse(JsonSerializer.Serialize(context)),
                    ["domain"] = JsonNode.Parse(JsonSerializer.Serialize(domain)),
                    ["topology"] = JsonNode.Parse(JsonSerializer.Serialize(topology)),
                },
            });
        }
        return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
            RuntimeValue.FromJson(JsonDocument.Parse(items.ToJsonString()).RootElement)));
    }
}
