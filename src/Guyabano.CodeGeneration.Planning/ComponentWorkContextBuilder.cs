namespace Guyabano.CodeGeneration.Planning;

internal sealed class ComponentWorkContextBuilder
    : IComponentWorkContextBuilder
{
    public ComponentWorkContext Build(
        CodeGenerationPlan plan,
        string parentTaskId,
        ResolvedDependencyContext resolvedDependencies)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentTaskId);
        ArgumentNullException.ThrowIfNull(resolvedDependencies);

        var task = plan.Tasks.Single(item => item.Id.Equals(
            parentTaskId,
            StringComparison.Ordinal));
        if (task.ExecutionKind != PlanTaskExecutionKind.CodeGeneration)
            throw new ArgumentException(
                $"Task '{parentTaskId}' is not a code-generation task.",
                nameof(parentTaskId));

        var module = plan.Modules.Single(item => item.Id.Equals(
            task.ModuleId,
            StringComparison.Ordinal));
        var project = plan.Projects.Single(item => item.Name.Equals(
            module.ProjectName,
            StringComparison.Ordinal));
        var criteria = SelectById(
            plan.AcceptanceCriteria,
            task.AcceptanceCriterionIds,
            item => item.Id);
        var useCaseIds = criteria.Select(item => item.UseCaseId)
            .ToHashSet(StringComparer.Ordinal);
        var affectedIds = task.ContractIds
            .Concat(task.DecisionIds)
            .Concat(task.AcceptanceCriterionIds)
            .Concat(useCaseIds)
            .Append(task.Id)
            .Append(module.Id)
            .Append(project.Name)
            .Append(plan.Solution.Name)
            .ToHashSet(StringComparer.Ordinal);
        var taskById = plan.Tasks.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        var moduleById = plan.Modules.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        var projectByName = plan.Projects.ToDictionary(
            item => item.Name,
            StringComparer.Ordinal);

        return new ComponentWorkContext(
            plan.Title,
            plan.Summary,
            plan.Mission,
            plan.Solution,
            task,
            module,
            project,
            project.ProjectDependencies
                .Select(name => projectByName[name])
                .Select(item => new ProjectDependencyContext(
                    item.Name,
                    item.Path,
                    item.Role))
                .ToArray(),
            plan.UseCases.Where(item => useCaseIds.Contains(item.Id))
                .ToArray(),
            criteria,
            SelectById(plan.Contracts, task.ContractIds, item => item.Id),
            SelectById(plan.Decisions, task.DecisionIds, item => item.Id),
            plan.ArchitectureNotes.Where(note =>
                    note.AffectedIds.Any(affectedIds.Contains))
                .ToArray(),
            task.DependsOn
                .Where(taskById.ContainsKey)
                .Select(id => CreateDependency(
                    id,
                    task,
                    taskById,
                    moduleById,
                    projectByName))
                .Where(item => item is not null)
                .Cast<ComponentWorkDependency>()
                .ToArray(),
            resolvedDependencies);
    }

    private static ComponentWorkDependency? CreateDependency(
        string dependencyId,
        GenerationTaskPlan task,
        IReadOnlyDictionary<string, GenerationTaskPlan> taskById,
        IReadOnlyDictionary<string, PlannedModule> moduleById,
        IReadOnlyDictionary<string, PlannedProject> projectByName)
    {
        var dependency = taskById[dependencyId];
        if (dependency.ExecutionKind != PlanTaskExecutionKind.CodeGeneration ||
            string.IsNullOrWhiteSpace(dependency.ModuleId) ||
            !moduleById.TryGetValue(dependency.ModuleId, out var module) ||
            !projectByName.TryGetValue(module.ProjectName, out var project))
            return null;

        return new ComponentWorkDependency(
            dependency.Id,
            dependency.Title,
            project.Name,
            project.Role,
            RelationshipKind(task.Relationships, dependency.Id),
            dependency.Deliverables);
    }

    private static ComponentDependencyKind RelationshipKind(
        ComponentRelationshipPlan relationships,
        string taskId)
    {
        if (relationships.UsesConcreteTaskIds.Contains(taskId, StringComparer.Ordinal))
            return ComponentDependencyKind.UsesConcreteComponent;
        if (relationships.RegistersImplementationTaskIds.Contains(taskId, StringComparer.Ordinal))
            return ComponentDependencyKind.RegistersImplementation;
        if (relationships.TestsTaskIds.Contains(taskId, StringComparer.Ordinal))
            return ComponentDependencyKind.TestsComponent;
        return ComponentDependencyKind.Prerequisite;
    }

    private static IReadOnlyList<T> SelectById<T>(
        IEnumerable<T> values,
        IEnumerable<string> ids,
        Func<T, string> idSelector)
    {
        var byId = values.ToDictionary(idSelector, StringComparer.Ordinal);
        return ids.Distinct(StringComparer.Ordinal)
            .Select(id => byId[id])
            .ToArray();
    }
}
