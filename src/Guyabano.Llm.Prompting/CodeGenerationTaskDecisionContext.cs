namespace Guyabano.Llm.Prompting;

public sealed record CodeGenerationTaskDecisionContext(
    string Id,
    string Title,
    string Decision,
    IReadOnlyList<string> Reasons);
