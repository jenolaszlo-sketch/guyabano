using System.Text.Json;
using System.Text.Json.Serialization;
using Guyabano.Artifacts;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// How to produce one artifact kind: pack, declared input kinds, output
/// schema, retry budget, and model profile. Definitions are data; the
/// generic runner executes them. The four software stages ship as built-in
/// definitions (<see cref="BuiltInPlanningStages"/>).
/// </summary>
public sealed record PlanningStageDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("artifactKind")]
    public required string ArtifactKind { get; init; }

    [JsonPropertyName("systemPack")]
    public required string SystemPack { get; init; }

    [JsonPropertyName("userPack")]
    public required string UserPack { get; init; }

    [JsonPropertyName("inputKinds")]
    public required IReadOnlyList<string> InputKinds { get; init; }

    [JsonPropertyName("outputSchema")]
    public required string OutputSchema { get; init; }

    [JsonPropertyName("maxAttempts")]
    public required int MaxAttempts { get; init; }

    [JsonPropertyName("modelProfile")]
    public required string ModelProfile { get; init; }
}

/// <summary>
/// A versioned set of stage definitions. The version fingerprints the set,
/// so a plan records exactly which catalogue it was built against and stale
/// plans are rejected like stale patch bases.
/// </summary>
public sealed record PlanningStageCatalogue
{
    public const string VersionScheme = "sha256:planning-stages/v1:";

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("definitions")]
    public required IReadOnlyList<PlanningStageDefinition> Definitions { get; init; }

    /// <summary>Builds a catalogue, ordering definitions by id and fingerprinting the set.</summary>
    public static PlanningStageCatalogue Create(
        IEnumerable<PlanningStageDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var ordered = definitions
            .OrderBy(definition => definition.Id, StringComparer.Ordinal)
            .ToArray();
        var duplicates = ordered.GroupBy(definition => definition.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Stage catalogue contains duplicate ids: {string.Join(", ", duplicates)}.");
        }

        foreach (var definition in ordered)
        {
            var errors = ValidateDefinition(definition);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Stage definition '{definition.Id}' is invalid: {string.Join(" ", errors)}");
            }
        }

        return new PlanningStageCatalogue
        {
            Version = VersionScheme + CanonicalJsonContentHash.Compute(
                JsonSerializer.SerializeToElement(ordered)),
            Definitions = ordered,
        };
    }

    internal static IReadOnlyList<string> ValidateDefinition(PlanningStageDefinition definition)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(definition.Id))
            errors.Add("Id must not be empty.");
        if (string.IsNullOrWhiteSpace(definition.ArtifactKind))
            errors.Add("ArtifactKind must not be empty.");
        if (string.IsNullOrWhiteSpace(definition.SystemPack))
            errors.Add("SystemPack must not be empty.");
        if (string.IsNullOrWhiteSpace(definition.UserPack))
            errors.Add("UserPack must not be empty.");
        if (string.IsNullOrWhiteSpace(definition.OutputSchema))
            errors.Add("OutputSchema must not be empty.");
        if (definition.MaxAttempts < 1)
            errors.Add("MaxAttempts must be at least 1.");
        if (string.IsNullOrWhiteSpace(definition.ModelProfile))
            errors.Add("ModelProfile must not be empty.");
        return errors;
    }
}
