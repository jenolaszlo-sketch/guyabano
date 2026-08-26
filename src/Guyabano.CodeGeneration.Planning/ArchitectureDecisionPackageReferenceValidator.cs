namespace Guyabano.CodeGeneration.Planning;

internal static class ArchitectureDecisionPackageReferenceValidator
{
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<PlannedProject> projects,
        ArchitectureDecision decision)
    {
        var packageNames = projects
            .SelectMany(project => project.Packages)
            .Select(package => package.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return decision.RelatedPackages
            .Where(packageName => !packageNames.Contains(packageName))
            .Select(packageName =>
                $"Decision '{decision.Id}' references unknown NuGet package '{packageName}'. relatedPackages accepts declared NuGet package IDs only; use an empty array when the decision involves no NuGet package.")
            .ToArray();
    }
}
