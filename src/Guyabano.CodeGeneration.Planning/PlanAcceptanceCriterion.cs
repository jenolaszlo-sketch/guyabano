using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class PlanAcceptanceCriterion
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("useCaseId")]
    public string UseCaseId { get; init; } = string.Empty;

    [JsonPropertyName("boundedContext")]
    public string BoundedContext { get; init; } = string.Empty;

    [JsonPropertyName("feature")]
    public required string Feature { get; init; }

    [JsonPropertyName("scenario")]
    public required string Scenario { get; init; }

    [JsonPropertyName("given")]
    public required List<string> Given { get; init; }

    [JsonPropertyName("when")]
    public required List<string> When { get; init; }

    [JsonPropertyName("then")]
    public required List<string> Then { get; init; }

    [JsonPropertyName("verificationKinds")]
    [SchemaDescription("Applicable verification kinds such as UnitTest, IntegrationTest, Compilation, StaticAnalysis, or ManualReview.")]
    public required List<string> VerificationKinds { get; init; }
}
