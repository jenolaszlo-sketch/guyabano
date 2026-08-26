namespace Guyabano.CodeGeneration.Planning;

public sealed class ResolvedDependencyContextBuilder
    : IResolvedDependencyContextBuilder
{
    public ResolvedDependencyContext Build(
        CodeGenerationPlan plan,
        string targetTaskId,
        IReadOnlyCollection<TaskDecompositionArtifactPayload>
            upstreamDecompositions)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTaskId);
        ArgumentNullException.ThrowIfNull(upstreamDecompositions);

        var tasks = plan.Tasks.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        if (!tasks.TryGetValue(targetTaskId, out var target))
            throw new ArgumentException(
                $"Target task '{targetTaskId}' does not exist.",
                nameof(targetTaskId));

        var dependencyIds = GetTransitiveDependencies(target, tasks);
        var decompositions = upstreamDecompositions.ToDictionary(
            item => item.ParentTaskId,
            StringComparer.Ordinal);
        var artifacts = new List<ResolvedArtifactDependency>();

        foreach (var dependency in OrderDependencies(
                     plan.Tasks,
                     dependencyIds))
        {
            if (dependency.ExecutionKind !=
                PlanTaskExecutionKind.CodeGeneration)
            {
                continue;
            }

            if (!decompositions.TryGetValue(
                    dependency.Id,
                    out var artifactPayload))
            {
                throw new InvalidOperationException(
                    $"Code-generation dependency '{dependency.Id}' has no validated decomposition artifact.");
            }

            if (artifactPayload.Decomposition.Status !=
                TaskDecompositionStatus.Ready)
            {
                throw new InvalidOperationException(
                    $"Dependency '{dependency.Id}' does not have a ready decomposition.");
            }

            foreach (var leaf in artifactPayload.Decomposition.LeafTasks)
            {
                foreach (var artifact in leaf.Artifacts)
                {
                    artifacts.Add(new(
                        dependency.Id,
                        leaf.Id,
                        artifact.Path,
                        artifact.Kind,
                        artifact.Namespace,
                        artifact.TypeNames.ToArray(),
                        leaf.ContractIds.ToArray(),
                        artifact.Requirements.ToArray()));
                }
            }
        }

        ValidateUniqueTypeOwnership(artifacts);
        var relatedContractIds = artifacts
            .SelectMany(item => item.RelatedContractIds)
            .ToHashSet(StringComparer.Ordinal);
        var contracts = plan.Contracts
            .Where(contract => relatedContractIds.Contains(contract.Id))
            .Select(contract => new ResolvedContractDependency(
                contract.Id,
                contract.Name,
                contract.Kind,
                contract.Purpose,
                contract.Members.ToArray()))
            .ToArray();
        return new ResolvedDependencyContext(artifacts, contracts);
    }

    private static HashSet<string> GetTransitiveDependencies(
        GenerationTaskPlan target,
        IReadOnlyDictionary<string, GenerationTaskPlan> tasks)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string taskId)
        {
            if (!result.Add(taskId))
                return;
            if (!tasks.TryGetValue(taskId, out var task))
                throw new InvalidOperationException(
                    $"Task dependency '{taskId}' does not exist.");
            foreach (var dependencyId in task.DependsOn)
                Visit(dependencyId);
        }

        foreach (var dependencyId in target.DependsOn)
            Visit(dependencyId);
        return result;
    }

    private static IReadOnlyList<GenerationTaskPlan> OrderDependencies(
        IReadOnlyList<GenerationTaskPlan> tasks,
        IReadOnlySet<string> dependencyIds)
    {
        var remaining = tasks
            .Where(task => dependencyIds.Contains(task.Id))
            .ToList();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<GenerationTaskPlan>(remaining.Count);

        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(task =>
                task.DependsOn.All(dependencyId =>
                    !dependencyIds.Contains(dependencyId) ||
                    completed.Contains(dependencyId)));
            if (ready is null)
                throw new InvalidOperationException(
                    "Task dependencies cannot be ordered for artifact projection.");

            ordered.Add(ready);
            completed.Add(ready.Id);
            remaining.Remove(ready);
        }

        return ordered;
    }

    private static void ValidateUniqueTypeOwnership(
        IReadOnlyCollection<ResolvedArtifactDependency> artifacts)
    {
        var owners = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            foreach (var typeName in artifact.FullyQualifiedTypeNames)
            {
                if (owners.TryGetValue(typeName, out var existingPath) &&
                    !existingPath.Equals(
                        artifact.Path,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Resolved type '{typeName}' is owned by both '{existingPath}' and '{artifact.Path}'.");
                }

                owners[typeName] = artifact.Path;
            }
        }
    }
}
