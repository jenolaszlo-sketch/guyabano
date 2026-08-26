namespace Guyabano.Llm.Prompting;

public sealed record CodeGenerationTaskContractContext(
    string Id,
    string Name,
    string Kind,
    string Purpose,
    IReadOnlyList<string> Members);
