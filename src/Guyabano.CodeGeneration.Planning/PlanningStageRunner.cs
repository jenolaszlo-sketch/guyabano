using System.Text.Json;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Inputs for one stage invocation: upstream outputs keyed by instance name.</summary>
public sealed record StageExecutionInput(
    PlannedStage Stage,
    PlanningStageDefinition Definition,
    IReadOnlyDictionary<string, JsonElement> UpstreamOutputs,
    string Request);

/// <summary>One stage invocation result; executors self-validate their output.</summary>
public sealed record StageExecutionResult(
    bool Succeeded,
    JsonElement? Output,
    IReadOnlyList<string> Diagnostics);

/// <summary>Produces one artifact kind. The generic runner orchestrates; executors do the work.</summary>
public interface IPlanningStageExecutor
{
    /// <summary>Definition id this executor serves.</summary>
    string StageId { get; }

    Task<StageExecutionResult> ExecuteAsync(
        StageExecutionInput input,
        CancellationToken cancellationToken = default);
}

/// <summary>One validated plan run: outputs and published versions by instance.</summary>
public sealed record PlanningStageRunResult(
    bool Succeeded,
    IReadOnlyDictionary<string, JsonElement> Outputs,
    IReadOnlyList<string> Published,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Executes admitted stage plans: validates, orders topologically, runs each
/// instance through its registered executor, and publishes every output as a
/// revisioned artifact chained to its inputs. Kind-agnostic: all
/// software-path knowledge lives in the definitions, not here.
/// </summary>
public sealed class PlanningStageRunner(
    PlanningStageCatalogue catalogue,
    IReadOnlyDictionary<string, IPlanningStageExecutor> executors,
    IPlanningArtifactCatalog artifactCatalog)
{
    private const int ArtifactSchemaVersion = 1;
    private const string ProducedBy = "planning-stage-runner";

    /// <summary>Definitions this runner executes.</summary>
    public PlanningStageCatalogue Catalogue => catalogue;

    /// <summary>
    /// Validates the plan, then runs and publishes it. Failures stop at the
    /// first failing stage with partial outputs preserved in the result.
    /// </summary>
    public async Task<PlanningStageRunResult> RunAsync(
        string workflowId,
        PlanningStagePlan plan,
        IReadOnlySet<string> knownRevisions,
        string request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(knownRevisions);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);

        var errors = PlanningStageValidator.Validate(plan, catalogue, knownRevisions);
        if (errors.Count > 0)
        {
            return new PlanningStageRunResult(false, new Dictionary<string, JsonElement>(), [], errors);
        }

        var definitions = catalogue.Definitions
            .ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        var ordered = PlanningStageValidator.Order(plan.Stages);
        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var published = new List<string>();
        var publishedVersions = new Dictionary<string, PlanningArtifactVersion>(StringComparer.Ordinal);

        foreach (var stage in ordered)
        {
            var definition = definitions[stage.StageId];
            if (!executors.TryGetValue(stage.StageId, out var executor))
            {
                return new PlanningStageRunResult(
                    false, outputs, published,
                    [$"No executor is registered for stage id '{stage.StageId}'."]);
            }

            var inputs = stage.DependsOn
                .ToDictionary(
                    dependency => dependency,
                    dependency => outputs[dependency],
                    StringComparer.Ordinal);
            var executed = await executor.ExecuteAsync(
                new StageExecutionInput(stage, definition, inputs, request), cancellationToken)
                .ConfigureAwait(false);
            if (!executed.Succeeded || executed.Output is null)
            {
                return new PlanningStageRunResult(
                    false, outputs, published,
                    [$"Stage instance '{stage.Name}' failed: " +
                     string.Join(" ", executed.Diagnostics)]);
            }

            var record = await artifactCatalog.PublishAsync(
                new PublishPlanningArtifactRequest<JsonElement>(
                    workflowId,
                    ParseKey(stage.Name),
                    ArtifactSchemaVersion,
                    ProducedBy,
                    executed.Output.Value,
                    StageInputs(stage, publishedVersions),
                    State: PlanningArtifactState.Valid),
                cancellationToken).ConfigureAwait(false);
            outputs[stage.Name] = executed.Output.Value;
            publishedVersions[stage.Name] = record.Version;
            published.Add(record.Version.Value);
        }

        return new PlanningStageRunResult(true, outputs, published, []);
    }

    private static PlanningArtifactKey ParseKey(string identity)
    {
        var slash = identity.IndexOf('/');
        return new PlanningArtifactKey(identity[..slash], identity[(slash + 1)..]);
    }

    private static IReadOnlyList<PlanningArtifactVersion> StageInputs(
        PlannedStage stage,
        IReadOnlyDictionary<string, PlanningArtifactVersion> publishedVersions)
    {
        var inputs = stage.DependsOn
            .Where(publishedVersions.ContainsKey)
            .Select(dependency => publishedVersions[dependency])
            .ToList();
        foreach (var pinned in stage.InputArtifacts)
        {
            var at = pinned.LastIndexOf('@');
            var key = pinned[..at];
            var slash = key.IndexOf('/');
            inputs.Add(new PlanningArtifactVersion(
                new PlanningArtifactKey(key[..slash], key[(slash + 1)..]),
                int.Parse(
                    pinned[(at + 1)..],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture)));
        }

        return inputs;
    }
}
