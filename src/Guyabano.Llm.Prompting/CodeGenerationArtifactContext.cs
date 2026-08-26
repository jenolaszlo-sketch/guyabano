namespace Guyabano.Llm.Prompting;

public sealed record CodeGenerationArtifactContext(
    string Path,
    string Kind,
    string Namespace,
    IReadOnlyList<string> TypeNames,
    IReadOnlyList<string> Requirements);
