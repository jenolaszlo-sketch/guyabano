using System.Text.Json;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Guyabano activity assembling the final <see cref="CodeGenerationPlan"/>
/// from admitted stage artifacts via
/// <c>StagedCodeGenerationPlanAssembler.Assemble</c>, which re-validates
/// the full cross-artifact contract (global uniqueness, coverage, cycles).
/// Accepts either separate <c>catalogs</c>/<c>manifests</c> lists or a
/// <c>pairs</c> list of <c>{catalog, manifest}</c> objects.
/// </summary>
public sealed class AssemblePlanningActivity : IActivityExecutor
{
    public ValueTask<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var domain = JsonSerializer.Deserialize<DomainDiscovery>(
            PlanningStageArguments.ReadActivityJson(request, "domain").GetRawText())
            ?? throw new InvalidOperationException("Assemble activity received an unreadable domain artifact.");
        var topology = JsonSerializer.Deserialize<SolutionTopology>(
            PlanningStageArguments.ReadActivityJson(request, "topology").GetRawText())
            ?? throw new InvalidOperationException("Assemble activity received an unreadable topology artifact.");
        var catalogs = ReadArtifacts<BoundedContextContractCatalog>(request, "catalogs", "catalog");
        var manifests = ReadArtifacts<BoundedContextComponentManifest>(request, "manifests", "manifest");
        var plan = StagedCodeGenerationPlanAssembler.Assemble(
            new StagedPlanningArtifacts(domain, topology, catalogs, manifests));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(plan));
        return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
            RuntimeValue.FromJson(document.RootElement)));
    }

    private static IReadOnlyList<T> ReadArtifacts<T>(
        ActivityExecutionRequest request, string listName, string pairField)
    {
        foreach (var argument in request.Arguments)
        {
            if (!string.Equals(argument.Name, listName, StringComparison.Ordinal) ||
                argument.Value is null)
            {
                continue;
            }
            var json = Penghou.Fuwen.RuntimeValueJson.ToJsonElement(argument.Value);
            if (json.ValueKind != JsonValueKind.Array)
                continue;
            return json.EnumerateArray()
                .Select(element => JsonSerializer.Deserialize<T>(element.GetRawText())
                    ?? throw new InvalidOperationException($"Assemble activity received an unreadable {typeof(T).Name}."))
                .ToArray();
        }
        foreach (var argument in request.Arguments)
        {
            if (!string.Equals(argument.Name, "pairs", StringComparison.Ordinal) ||
                argument.Value is null)
            {
                continue;
            }
            var json = Penghou.Fuwen.RuntimeValueJson.ToJsonElement(argument.Value);
            if (json.ValueKind != JsonValueKind.Array)
                continue;
            return json.EnumerateArray()
                .Select(element => JsonSerializer.Deserialize<T>(element.GetProperty(pairField).GetRawText())
                    ?? throw new InvalidOperationException($"Assemble activity received an unreadable {typeof(T).Name}."))
                .ToArray();
        }
        throw new InvalidOperationException(
            $"Assemble activity requires '{listName}' or 'pairs' arguments.");
    }
}
