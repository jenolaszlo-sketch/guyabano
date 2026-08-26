namespace Guyabano.CodeGeneration.Planning;

internal static class CodeGenerationTaskDecompositionValidator
{
    private static readonly HashSet<int> AllowedPoints = [1, 2];

    public static IReadOnlyList<string> Validate(
        CodeGenerationPlan plan,
        GenerationTaskPlan parent,
        CodeGenerationTaskDecomposition decomposition,
        ResolvedDependencyContext? resolvedDependencies = null) =>
        Validate(
            new ComponentWorkContextBuilder().Build(
                plan,
                parent.Id,
                resolvedDependencies ?? ResolvedDependencyContext.Empty),
            decomposition,
            resolvedDependencies);

    public static IReadOnlyList<string> Validate(
        ComponentWorkContext workContext,
        CodeGenerationTaskDecomposition decomposition,
        ResolvedDependencyContext? resolvedDependencies = null)
    {
        var errors = new List<string>();
        var parent = workContext.ParentTask;

        if (!decomposition.ParentTaskId.Equals(
                parent.Id,
                StringComparison.Ordinal))
        {
            errors.Add(
                $"Decomposition parent '{decomposition.ParentTaskId}' does not match task '{parent.Id}'.");
        }

        if (decomposition.Status == TaskDecompositionStatus.Ready)
        {
            if (decomposition.LeafTasks.Count == 0)
                errors.Add("A ready decomposition contains no leaf tasks.");
            if (decomposition.ArchitectureGaps.Count > 0)
                errors.Add("A ready decomposition must not contain architecture gaps.");
        }
        else
        {
            if (decomposition.ArchitectureGaps.Count == 0)
                errors.Add("An ArchitectureGap result contains no gap details.");
            if (decomposition.LeafTasks.Count > 0)
                errors.Add("An ArchitectureGap result must not contain leaf tasks.");
        }

        ValidateGaps(workContext, decomposition, errors);
        if (decomposition.Status == TaskDecompositionStatus.Ready)
            ValidateLeaves(
                workContext,
                decomposition.LeafTasks,
                resolvedDependencies ?? ResolvedDependencyContext.Empty,
                errors);
        return errors;
    }

    private static void ValidateGaps(
        ComponentWorkContext workContext,
        CodeGenerationTaskDecomposition decomposition,
        ICollection<string> errors)
    {
        var contracts = workContext.Contracts.Select(item => item.Id)
            .Concat(workContext.ResolvedDependencies.EffectiveContracts
                .Select(item => item.Id))
            .ToHashSet(StringComparer.Ordinal);
        var decisions = workContext.Decisions.Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var gap in decomposition.ArchitectureGaps)
        {
            if (string.IsNullOrWhiteSpace(gap.Question) ||
                string.IsNullOrWhiteSpace(gap.Reason))
                errors.Add("An architecture gap has an empty question or reason.");

            ValidateReferences(
                "architecture gap",
                "contract",
                gap.AffectedContractIds,
                contracts,
                errors);
            ValidateReferences(
                "architecture gap",
                "decision",
                gap.AffectedDecisionIds,
                decisions,
                errors);
        }
    }

    private static void ValidateLeaves(
        ComponentWorkContext workContext,
        IReadOnlyCollection<CodeGenerationLeafTask> leaves,
        ResolvedDependencyContext resolvedDependencies,
        ICollection<string> errors)
    {
        var parent = workContext.ParentTask;
        var leafIds = new HashSet<string>(StringComparer.Ordinal);
        var artifactPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var parentContracts = parent.ContractIds.ToHashSet(
            StringComparer.Ordinal);
        var availableContracts = parentContracts
            .Concat(resolvedDependencies.EffectiveContracts.Select(item => item.Id))
            .ToHashSet(StringComparer.Ordinal);
        var parentCriteria = parent.AcceptanceCriterionIds.ToHashSet(
            StringComparer.Ordinal);
        var decisions = parent.DecisionIds.ToHashSet(
            StringComparer.Ordinal);
        var project = workContext.Project;
        var projectDirectory = Normalize(
            Path.GetDirectoryName(project.Path) ?? string.Empty);

        foreach (var leaf in leaves)
        {
            if (string.IsNullOrWhiteSpace(leaf.Id) ||
                !leafIds.Add(leaf.Id))
                errors.Add($"Duplicate or empty leaf task ID '{leaf.Id}'.");
            else if (!leaf.Id.StartsWith(
                         $"{parent.Id}-L",
                         StringComparison.Ordinal))
                errors.Add($"Leaf task '{leaf.Id}' must be prefixed with '{parent.Id}-L'.");
            if (!AllowedPoints.Contains(leaf.ComplexityPoints))
                errors.Add($"Leaf task '{leaf.Id}' must use 1 or 2 complexity points.");
            if (leaf.Artifacts.Count == 0)
                errors.Add($"Leaf task '{leaf.Id}' contains no artifacts.");
            if (leaf.ImplementationRequirements.Count == 0)
                errors.Add($"Leaf task '{leaf.Id}' contains no implementation requirements.");

            ValidateReferences(
                leaf.Id,
                "contract",
                leaf.ContractIds,
                availableContracts,
                errors);
            ValidateReferences(
                leaf.Id,
                "acceptance criterion",
                leaf.AcceptanceCriterionIds,
                parentCriteria,
                errors);
            ValidateReferences(
                leaf.Id,
                "decision",
                leaf.DecisionIds,
                decisions,
                errors);

            foreach (var artifact in leaf.Artifacts)
            {
                var path = Normalize(artifact.Path);
                if (string.IsNullOrWhiteSpace(path) ||
                    !artifactPaths.Add(path))
                    errors.Add($"Duplicate or empty artifact path '{artifact.Path}'.");
                if (!IsWithin(path, projectDirectory))
                    errors.Add($"Leaf task '{leaf.Id}' artifact '{artifact.Path}' is outside project '{project.Name}'.");
                if (HasUnsafeSegment(path))
                    errors.Add($"Leaf task '{leaf.Id}' artifact '{artifact.Path}' contains an unsafe path segment.");
                if (path.Equals(
                        Normalize(project.Path),
                        StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Leaf task '{leaf.Id}' must not modify project files.");
                if (IsBuildOrPackageArtifact(path))
                    errors.Add($"Leaf task '{leaf.Id}' must not modify build or package-management artifact '{artifact.Path}'.");
                if (artifact.Requirements.Count == 0)
                    errors.Add($"Artifact '{artifact.Path}' contains no requirements.");
            }
        }

        foreach (var leaf in leaves)
        {
            ValidateReferences(
                leaf.Id,
                "sibling dependency",
                leaf.DependsOn,
                leafIds,
                errors);
            if (leaf.DependsOn.Contains(leaf.Id, StringComparer.Ordinal))
                errors.Add($"Leaf task '{leaf.Id}' depends on itself.");
        }

        ValidateCoverage(
            parent.Id,
            "contract",
            parentContracts,
            leaves.SelectMany(item => item.ContractIds),
            errors);
        ValidateCoverage(
            parent.Id,
            "acceptance criterion",
            parentCriteria,
            leaves.SelectMany(item => item.AcceptanceCriterionIds),
            errors);
        ValidateGraph(leaves, errors);
    }

    private static void ValidateCoverage(
        string parentId,
        string kind,
        IReadOnlySet<string> expected,
        IEnumerable<string> actual,
        ICollection<string> errors)
    {
        var assigned = actual.ToHashSet(StringComparer.Ordinal);
        foreach (var id in expected.Where(id => !assigned.Contains(id)))
            errors.Add($"Parent task '{parentId}' {kind} '{id}' is not assigned to a leaf task.");
    }

    private static void ValidateReferences(
        string owner,
        string kind,
        IEnumerable<string> references,
        IReadOnlySet<string> known,
        ICollection<string> errors)
    {
        foreach (var reference in references.Where(item => !known.Contains(item)))
            errors.Add($"{owner} references unknown {kind} '{reference}'.");
    }

    private static void ValidateGraph(
        IReadOnlyCollection<CodeGenerationLeafTask> leaves,
        ICollection<string> errors)
    {
        var dependencies = leaves.ToDictionary(
            item => item.Id,
            item => item.DependsOn,
            StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        bool HasCycle(string id)
        {
            if (visited.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            if (dependencies.TryGetValue(id, out var values) &&
                values.Any(HasCycle)) return true;
            visiting.Remove(id);
            visited.Add(id);
            return false;
        }

        if (leaves.Any(item => HasCycle(item.Id)))
            errors.Add("The leaf task dependency graph contains a cycle.");
    }

    private static bool IsWithin(string path, string directory) =>
        !string.IsNullOrWhiteSpace(directory) &&
        (path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith($"{directory}/", StringComparison.OrdinalIgnoreCase));

    private static bool HasUnsafeSegment(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");

    private static bool IsBuildOrPackageArtifact(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".csproj" or ".sln" or ".slnx" or ".props" or ".targets";

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
