using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Host policy bounding one planning loop run. Count bounds are hard and
/// deterministic; <see cref="MaxTotalTokens"/> is best-effort because model
/// usage is only reported when the provider reports it.
/// </summary>
public sealed record PlanningPolicy
{
    [JsonPropertyName("maxIterations")]
    public required int MaxIterations { get; init; }

    [JsonPropertyName("maxMutations")]
    public required int MaxMutations { get; init; }

    [JsonPropertyName("maxWorkflowNodes")]
    public required int MaxWorkflowNodes { get; init; }

    [JsonPropertyName("maxPlanningDepth")]
    public required int MaxPlanningDepth { get; init; }

    [JsonPropertyName("maxModelCalls")]
    public required int MaxModelCalls { get; init; }

    [JsonPropertyName("maxConsecutiveNoOps")]
    public required int MaxConsecutiveNoOps { get; init; }

    /// <summary>Null disables the token bound.</summary>
    [JsonPropertyName("maxTotalTokens")]
    public int? MaxTotalTokens { get; init; }

    /// <summary>Sensible starting bounds for a small planning run.</summary>
    public static PlanningPolicy Default => new()
    {
        MaxIterations = 10,
        MaxMutations = 5,
        MaxWorkflowNodes = 50,
        MaxPlanningDepth = 8,
        MaxModelCalls = 30,
        MaxConsecutiveNoOps = 2,
        MaxTotalTokens = null,
    };

    /// <summary>Validates bound values; returns error descriptions, empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaxIterations < 1)
            errors.Add("MaxIterations must be at least 1.");
        if (MaxMutations < 0)
            errors.Add("MaxMutations must not be negative.");
        if (MaxWorkflowNodes < 1)
            errors.Add("MaxWorkflowNodes must be at least 1.");
        if (MaxPlanningDepth < 0)
            errors.Add("MaxPlanningDepth must not be negative.");
        if (MaxModelCalls < 1)
            errors.Add("MaxModelCalls must be at least 1.");
        if (MaxConsecutiveNoOps < 1)
            errors.Add("MaxConsecutiveNoOps must be at least 1.");
        if (MaxTotalTokens is <= 0)
            errors.Add("MaxTotalTokens must be positive when set.");
        return errors;
    }
}
