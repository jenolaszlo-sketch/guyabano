namespace Guyabano.CodeGeneration.Planning;

public sealed record ResolvedDependencyContext(
    IReadOnlyList<ResolvedArtifactDependency> Artifacts,
    IReadOnlyList<ResolvedContractDependency>? Contracts = null)
{
    public static ResolvedDependencyContext Empty { get; } =
        new([], []);

    public IReadOnlyList<ResolvedContractDependency> EffectiveContracts =>
        Contracts ?? [];
}
