using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class DiscoveredAcceptanceCriterion
{
    [JsonPropertyName("scenario")]
    public required string Scenario { get; init; }

    [JsonPropertyName("given")]
    public required List<string> Given { get; init; }

    [JsonPropertyName("when")]
    public required List<string> When { get; init; }

    [JsonPropertyName("then")]
    public required List<string> Then { get; init; }

    [JsonPropertyName("verificationKinds")]
    public required List<string> VerificationKinds { get; init; }
}
