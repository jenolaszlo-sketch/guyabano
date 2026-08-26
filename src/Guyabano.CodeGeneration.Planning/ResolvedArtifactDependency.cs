namespace Guyabano.CodeGeneration.Planning;

public sealed record ResolvedArtifactDependency(
    string ArchitectureTaskId,
    string LeafTaskId,
    string Path,
    string Kind,
    string Namespace,
    IReadOnlyList<string> TypeNames,
    IReadOnlyList<string> RelatedContractIds,
    IReadOnlyList<string>? Requirements = null)
{
    public IReadOnlyList<string> FullyQualifiedTypeNames =>
        TypeNames
            .Select(typeName =>
                string.IsNullOrWhiteSpace(Namespace) ||
                typeName.StartsWith(
                    $"{Namespace}.",
                    StringComparison.Ordinal)
                    ? typeName
                    : $"{Namespace}.{typeName}")
            .ToArray();

    public IReadOnlyList<string> EffectiveRequirements =>
        Requirements ?? [];
}
