namespace Guyabano.CodeGeneration.Planning;

public sealed record ResolvedContractDependency(
    string Id,
    string Name,
    string Kind,
    string Purpose,
    IReadOnlyList<string> Members);
