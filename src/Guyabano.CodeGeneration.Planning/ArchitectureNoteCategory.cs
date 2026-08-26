using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<ArchitectureNoteCategory>))]
public enum ArchitectureNoteCategory
{
    PlatformConvention,
    BestPractice,
    InferredDomainConstraint,
    InferredDefault,
    TechnicalChoice,
    DeferredDecision,
    UserClarificationRequired
}
