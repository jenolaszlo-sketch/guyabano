namespace Guyabano.Llm.Prompting;

public sealed record CodeGenerationTaskAcceptanceContext(
    string Id,
    string Feature,
    string Scenario,
    IReadOnlyList<string> Given,
    IReadOnlyList<string> When,
    IReadOnlyList<string> Then);
