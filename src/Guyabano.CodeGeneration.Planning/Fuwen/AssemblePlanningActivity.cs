using System.Text.Json;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Guyabano activity assembling the final <see cref="CodeGenerationPlan"/>
/// from admitted stage artifacts via
/// <c>StagedCodeGenerationPlanAssembler.Assemble</c>, which re-validates
/// the full cross-artifact contract (global uniqueness, coverage, cycles).
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
        var catalogs = PlanningStageArguments.ReadActivityJson(request, "catalogs").EnumerateArray()
            .Select(element => JsonSerializer.Deserialize<BoundedContextContractCatalog>(element.GetRawText())
                ?? throw new InvalidOperationException("Assemble activity received an unreadable contract catalog."))
            .ToArray();
        var manifests = PlanningStageArguments.ReadActivityJson(request, "manifests").EnumerateArray()
            .Select(element => JsonSerializer.Deserialize<BoundedContextComponentManifest>(element.GetRawText())
                ?? throw new InvalidOperationException("Assemble activity received an unreadable component manifest."))
            .ToArray();
        var plan = StagedCodeGenerationPlanAssembler.Assemble(
            new StagedPlanningArtifacts(domain, topology, catalogs, manifests));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(plan));
        return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
            RuntimeValue.FromJson(document.RootElement)));
    }
}
