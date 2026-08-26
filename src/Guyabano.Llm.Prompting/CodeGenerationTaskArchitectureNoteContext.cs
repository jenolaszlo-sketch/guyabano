namespace Guyabano.Llm.Prompting;

public sealed record CodeGenerationTaskArchitectureNoteContext(
    string Id,
    string Category,
    string Subject,
    string Decision,
    string Impact,
    IReadOnlyList<string> Reasons);
