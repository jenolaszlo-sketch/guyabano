namespace Guyabano.CodeGeneration.Planning;

internal static class CodeGenerationPlanValidator
{
    private static readonly HashSet<int> AllowedComplexityPoints =
        [1, 2, 3, 5, 8, 13];

    public static IReadOnlyList<string> Validate(CodeGenerationPlan plan)
    {
        var errors = new List<string>();

        if (plan.Projects.Count == 0)
            errors.Add("The plan contains no projects.");
        if (plan.Modules.Count == 0)
            errors.Add("The plan contains no modules.");
        if (plan.Tasks.Count == 0)
            errors.Add("The plan contains no implementation tasks.");
        if (plan.AcceptanceCriteria.Count == 0)
            errors.Add("The plan contains no acceptance criteria.");
        if (string.IsNullOrWhiteSpace(plan.Mission.GuidingIntent) ||
            plan.Mission.SuccessOutcomes.Count == 0)
            errors.Add("The plan contains no guiding mission or success outcomes.");
        if (plan.UseCases.Count == 0)
            errors.Add("The plan contains no use cases.");

        if (string.IsNullOrWhiteSpace(plan.Solution.Name))
            errors.Add("The solution has an empty name.");
        if (string.IsNullOrWhiteSpace(plan.Solution.Path))
            errors.Add("The solution has an empty path.");

        ValidateUniqueIds(plan.Projects.Select(item => item.Name), "project name", errors);
        ValidateUniqueIds(plan.Projects.Select(item => item.Path), "project path", errors);
        ValidateUniqueIds(plan.Modules.Select(item => item.Id), "module", errors);
        ValidateUniqueIds(plan.Contracts.Select(item => item.Id), "contract", errors);
        ValidateUniqueIds(plan.Decisions.Select(item => item.Id), "decision", errors);
        ValidateUniqueIds(plan.ArchitectureNotes.Select(item => item.Id), "architecture note", errors);
        ValidateUniqueIds(plan.UseCases.Select(item => item.Id), "use case", errors);
        ValidateUniqueIds(plan.AcceptanceCriteria.Select(item => item.Id), "acceptance criterion", errors);
        ValidateUniqueIds(plan.Tasks.Select(item => item.Id), "task", errors);

        var projectNames = plan.Projects
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var moduleIds = plan.Modules
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var contractIds = plan.Contracts
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var criterionIds = plan.AcceptanceCriteria
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var useCaseIds = plan.UseCases
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var decisionIds = plan.Decisions
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var taskIds = plan.Tasks
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        var architectureIds = projectNames
            .Concat(moduleIds)
            .Concat(contractIds)
            .Concat(decisionIds)
            .Concat(useCaseIds)
            .Concat(criterionIds)
            .Concat(taskIds)
            .Append(plan.Solution.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var project in plan.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.TargetFramework))
                errors.Add($"Project '{project.Name}' has an empty target framework.");
            if (!Enum.IsDefined(project.Role))
                errors.Add($"Project '{project.Name}' uses unsupported role '{project.Role}'.");

            foreach (var dependency in project.ProjectDependencies)
            {
                if (!projectNames.Contains(dependency))
                    errors.Add($"Project '{project.Name}' references unknown project '{dependency}'.");
                if (dependency.Equals(project.Name, StringComparison.Ordinal))
                    errors.Add($"Project '{project.Name}' references itself.");
            }

            ValidateUniqueIds(
                project.Packages.Select(package => package.Name),
                $"package in project '{project.Name}'",
                errors);
        }

        ValidateProjectRoles(plan.Projects, errors);

        foreach (var module in plan.Modules)
        {
            if (!projectNames.Contains(module.ProjectName))
                errors.Add($"Module '{module.Id}' references unknown project '{module.ProjectName}'.");
            if (string.IsNullOrWhiteSpace(module.BoundedContext))
                errors.Add($"Module '{module.Id}' has no bounded-context ownership.");
        }

        foreach (var useCase in plan.UseCases)
        {
            if (string.IsNullOrWhiteSpace(useCase.Name) ||
                string.IsNullOrWhiteSpace(useCase.Capability) ||
                string.IsNullOrWhiteSpace(useCase.BoundedContext) ||
                string.IsNullOrWhiteSpace(useCase.Actor) ||
                string.IsNullOrWhiteSpace(useCase.Objective) ||
                useCase.Outcomes.Count == 0)
                errors.Add($"Use case '{useCase.Id}' is incomplete.");
            ValidateReferences(
                useCase.Id,
                "acceptance criterion",
                useCase.AcceptanceCriterionIds,
                criterionIds,
                errors);
            if (useCase.AcceptanceCriterionIds.Count == 0)
                errors.Add($"Use case '{useCase.Id}' has no acceptance criteria.");
        }

        foreach (var criterion in plan.AcceptanceCriteria)
        {
            if (!useCaseIds.Contains(criterion.UseCaseId))
                errors.Add($"Acceptance criterion '{criterion.Id}' references unknown use case '{criterion.UseCaseId}'.");
            else
            {
                var owner = plan.UseCases.First(item =>
                    item.Id.Equals(criterion.UseCaseId, StringComparison.Ordinal));
                if (!owner.BoundedContext.Equals(
                        criterion.BoundedContext,
                        StringComparison.Ordinal))
                    errors.Add($"Acceptance criterion '{criterion.Id}' does not share bounded-context ownership with use case '{criterion.UseCaseId}'.");
            }
        }

        foreach (var contract in plan.Contracts)
        {
            if (!moduleIds.Contains(contract.ModuleId))
                errors.Add($"Contract '{contract.Id}' references unknown module '{contract.ModuleId}'.");
        }

        foreach (var decision in plan.Decisions)
            errors.AddRange(
                ArchitectureDecisionPackageReferenceValidator.Validate(
                    plan.Projects,
                    decision));

        foreach (var note in plan.ArchitectureNotes)
        {
            if (!Enum.IsDefined(note.Category))
                errors.Add($"Architecture note '{note.Id}' uses unsupported category '{note.Category}'.");
            if (string.IsNullOrWhiteSpace(note.Subject) ||
                string.IsNullOrWhiteSpace(note.MissingInformation) ||
                string.IsNullOrWhiteSpace(note.Decision) ||
                string.IsNullOrWhiteSpace(note.Impact))
                errors.Add($"Architecture note '{note.Id}' is incomplete.");
            if (note.Reasons.Count == 0 ||
                note.Reasons.Any(string.IsNullOrWhiteSpace))
                errors.Add($"Architecture note '{note.Id}' contains no usable reason.");
            if (note.AffectedIds.Count == 0)
                errors.Add($"Architecture note '{note.Id}' references no affected architecture ID.");
            foreach (var affectedId in note.AffectedIds.Where(id =>
                         !architectureIds.Contains(id)))
                errors.Add($"Architecture note '{note.Id}' references unknown architecture ID '{affectedId}'.");
        }

        var scaffoldingTasks = plan.Tasks
            .Where(task => task.ExecutionKind == PlanTaskExecutionKind.Scaffolding)
            .ToList();
        if (scaffoldingTasks.Count > 1)
            errors.Add("The plan contains more than one scaffolding task.");

        foreach (var task in plan.Tasks)
        {
            if (!Enum.IsDefined(task.ExecutionKind))
                errors.Add($"Task '{task.Id}' uses unsupported execution kind '{task.ExecutionKind}'.");

            if (task.ExecutionKind == PlanTaskExecutionKind.Scaffolding)
            {
                if (!string.IsNullOrWhiteSpace(task.ModuleId))
                    errors.Add($"Scaffolding task '{task.Id}' must not reference a module.");
                if (task.DependsOn.Count > 0)
                    errors.Add($"Scaffolding task '{task.Id}' must not depend on another task.");
            }
            else if (string.IsNullOrWhiteSpace(task.ModuleId))
            {
                errors.Add($"Code-generation task '{task.Id}' must reference a module.");
            }
            else if (!moduleIds.Contains(task.ModuleId))
            {
                errors.Add($"Task '{task.Id}' references unknown module '{task.ModuleId}'.");
            }
            else
            {
                var module = plan.Modules.First(item =>
                    item.Id.Equals(task.ModuleId, StringComparison.Ordinal));
                if (!module.BoundedContext.Equals(
                        task.BoundedContext,
                        StringComparison.Ordinal))
                    errors.Add($"Task '{task.Id}' does not share bounded-context ownership with module '{task.ModuleId}'.");
            }

            if (!AllowedComplexityPoints.Contains(task.ComplexityPoints))
                errors.Add($"Task '{task.Id}' uses unsupported complexity points '{task.ComplexityPoints}'.");
            if (task.EstimatedFiles < 0)
                errors.Add($"Task '{task.Id}' has a negative estimated file count.");

            ValidateReferences(task.Id, "dependency", task.DependsOn, taskIds, errors);
            ValidateReferences(task.Id, "contract", task.ContractIds, contractIds, errors);
            ValidateReferences(task.Id, "decision", task.DecisionIds, decisionIds, errors);
            ValidateReferences(task.Id, "acceptance criterion", task.AcceptanceCriterionIds, criterionIds, errors);

            ValidateTaskContractProjectDependencies(plan, task, errors);
            ValidateTypedRelationships(task, errors);

            if (task.DependsOn.Contains(task.Id, StringComparer.Ordinal))
                errors.Add($"Task '{task.Id}' depends on itself.");
        }

        if (scaffoldingTasks.Count == 1)
            ValidateScaffoldingDependency(scaffoldingTasks[0], plan.Tasks, errors);

        var assignedCriterionIds = plan.Tasks
            .SelectMany(task => task.AcceptanceCriterionIds)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var criterion in plan.AcceptanceCriteria)
        {
            if (!assignedCriterionIds.Contains(criterion.Id))
                errors.Add($"Acceptance criterion '{criterion.Id}' is not assigned to a task.");
        }

        ValidateProjectGraph(plan.Projects, errors);
        ValidateTaskGraph(plan.Tasks, errors);
        return errors;
    }

    private static void ValidateTypedRelationships(
        GenerationTaskPlan task,
        ICollection<string> errors)
    {
        var relationships = task.Relationships;
        var typedContracts = relationships.DefinesContractIds
            .Concat(relationships.ImplementsPortContractIds)
            .Concat(relationships.ConsumesContractIds)
            .ToArray();
        var typedDependencies = relationships.UsesConcreteTaskIds
            .Concat(relationships.RegistersImplementationTaskIds)
            .Concat(relationships.TestsTaskIds)
            .ToArray();

        foreach (var contractId in typedContracts.Where(id =>
                     !task.ContractIds.Contains(id, StringComparer.Ordinal)))
            errors.Add(
                $"Task '{task.Id}' has typed relationship to unassigned contract '{contractId}'.");
        foreach (var dependencyId in typedDependencies.Where(id =>
                     !task.DependsOn.Contains(id, StringComparer.Ordinal)))
            errors.Add(
                $"Task '{task.Id}' has typed relationship to non-dependency task '{dependencyId}'.");

        foreach (var duplicate in typedDependencies
                     .GroupBy(item => item, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            errors.Add(
                $"Task '{task.Id}' assigns multiple component relationship types to '{duplicate.Key}'.");
    }

    private static void ValidateProjectRoles(
        IReadOnlyCollection<PlannedProject> projects,
        ICollection<string> errors)
    {
        var byName = projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Name))
            .GroupBy(project => project.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var project in projects)
        {
            foreach (var dependencyName in project.ProjectDependencies)
            {
                if (!byName.TryGetValue(dependencyName, out var dependency))
                    continue;
                if (project.Role != ProjectRole.Test && dependency.Role == ProjectRole.Test)
                    errors.Add(
                        $"Production project '{project.Name}' must not depend on test project '{dependency.Name}'.");
                if (!IsAllowedRoleDependency(project.Role, dependency.Role))
                    errors.Add(
                        $"Project '{project.Name}' with role '{project.Role}' must not depend on project '{dependency.Name}' with role '{dependency.Role}'.");
            }
        }
    }

    private static bool IsAllowedRoleDependency(
        ProjectRole source,
        ProjectRole target) => source switch
        {
            ProjectRole.Contracts => target == ProjectRole.Contracts,
            ProjectRole.Domain => target is ProjectRole.Domain or ProjectRole.Contracts,
            ProjectRole.Application => target is ProjectRole.Application or
                ProjectRole.Domain or ProjectRole.Contracts,
            ProjectRole.Adapter => target is ProjectRole.Application or
                ProjectRole.Domain or ProjectRole.Contracts,
            ProjectRole.CompositionRoot => target != ProjectRole.Test &&
                target != ProjectRole.Tooling,
            ProjectRole.Test => target != ProjectRole.Tooling,
            ProjectRole.Tooling => true,
            _ => false
        };

    private static void ValidateTaskContractProjectDependencies(
        CodeGenerationPlan plan,
        GenerationTaskPlan task,
        ICollection<string> errors)
    {
        if (task.ExecutionKind != PlanTaskExecutionKind.CodeGeneration ||
            string.IsNullOrWhiteSpace(task.ModuleId))
            return;

        var taskModule = plan.Modules.FirstOrDefault(module =>
            module.Id.Equals(task.ModuleId, StringComparison.Ordinal));
        if (taskModule is null)
            return;

        var projects = plan.Projects.ToDictionary(
            project => project.Name,
            StringComparer.Ordinal);
        if (!projects.ContainsKey(taskModule.ProjectName))
            return;

        foreach (var contractId in task.ContractIds)
        {
            var contract = plan.Contracts.FirstOrDefault(item =>
                item.Id.Equals(contractId, StringComparison.Ordinal));
            var contractModule = contract is null
                ? null
                : plan.Modules.FirstOrDefault(module =>
                    module.Id.Equals(
                        contract.ModuleId,
                        StringComparison.Ordinal));
            if (contractModule is null ||
                contractModule.ProjectName.Equals(
                    taskModule.ProjectName,
                    StringComparison.Ordinal) ||
                CanReachProject(
                    taskModule.ProjectName,
                    contractModule.ProjectName,
                    projects,
                    new HashSet<string>(StringComparer.Ordinal)))
                continue;

            errors.Add(
                $"Task '{task.Id}' in project '{taskModule.ProjectName}' consumes contract '{contractId}' from project '{contractModule.ProjectName}', but no project dependency path exists.");
        }
    }

    private static bool CanReachProject(
        string projectName,
        string requiredProjectName,
        IReadOnlyDictionary<string, PlannedProject> projects,
        ISet<string> visited)
    {
        if (!visited.Add(projectName) ||
            !projects.TryGetValue(projectName, out var project))
            return false;

        return project.ProjectDependencies.Contains(
                   requiredProjectName,
                   StringComparer.Ordinal) ||
               project.ProjectDependencies.Any(dependency =>
                   CanReachProject(
                       dependency,
                       requiredProjectName,
                       projects,
                       visited));
    }

    private static void ValidateScaffoldingDependency(
        GenerationTaskPlan scaffoldingTask,
        IReadOnlyCollection<GenerationTaskPlan> tasks,
        ICollection<string> errors)
    {
        var dependencies = tasks.ToDictionary(
            task => task.Id,
            task => task.DependsOn,
            StringComparer.Ordinal);

        foreach (var task in tasks.Where(task =>
                     task.ExecutionKind == PlanTaskExecutionKind.CodeGeneration))
        {
            if (!DependsOn(
                    task.Id,
                    scaffoldingTask.Id,
                    dependencies,
                    new HashSet<string>(StringComparer.Ordinal)))
                errors.Add($"Code-generation task '{task.Id}' does not depend on scaffolding task '{scaffoldingTask.Id}'.");
        }
    }

    private static bool DependsOn(
        string taskId,
        string expectedDependencyId,
        IReadOnlyDictionary<string, List<string>> dependencies,
        ISet<string> visited)
    {
        if (!visited.Add(taskId) ||
            !dependencies.TryGetValue(taskId, out var taskDependencies))
            return false;

        return taskDependencies.Contains(expectedDependencyId, StringComparer.Ordinal) ||
               taskDependencies.Any(dependency =>
                   DependsOn(dependency, expectedDependencyId, dependencies, visited));
    }

    private static void ValidateUniqueIds(
        IEnumerable<string> ids,
        string kind,
        ICollection<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                errors.Add($"A {kind} has an empty ID.");
            else if (!seen.Add(id))
                errors.Add($"Duplicate {kind} ID '{id}'.");
        }
    }

    private static void ValidateReferences(
        string taskId,
        string kind,
        IEnumerable<string> references,
        IReadOnlySet<string> knownIds,
        ICollection<string> errors)
    {
        foreach (var reference in references)
        {
            if (!knownIds.Contains(reference))
                errors.Add($"Task '{taskId}' references unknown {kind} '{reference}'.");
        }
    }

    private static void ValidateTaskGraph(
        IReadOnlyCollection<GenerationTaskPlan> tasks,
        ICollection<string> errors)
    {
        var dependencies = tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.Id))
            .GroupBy(task => task.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().DependsOn,
                StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            if (HasCycle(task.Id, dependencies, visiting, visited))
            {
                errors.Add("The task dependency graph contains a cycle.");
                return;
            }
        }
    }

    private static void ValidateProjectGraph(
        IReadOnlyCollection<PlannedProject> projects,
        ICollection<string> errors)
    {
        var dependencies = projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Name))
            .GroupBy(project => project.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().ProjectDependencies,
                StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            if (HasCycle(project.Name, dependencies, visiting, visited))
            {
                errors.Add("The project dependency graph contains a cycle.");
                return;
            }
        }
    }

    private static bool HasCycle(
        string id,
        IReadOnlyDictionary<string, List<string>> dependencies,
        ISet<string> visiting,
        ISet<string> visited)
    {
        if (visited.Contains(id))
            return false;
        if (!visiting.Add(id))
            return true;

        if (dependencies.TryGetValue(id, out var itemDependencies))
        {
            foreach (var dependency in itemDependencies)
            {
                if (dependencies.ContainsKey(dependency) &&
                    HasCycle(dependency, dependencies, visiting, visited))
                    return true;
            }
        }

        visiting.Remove(id);
        visited.Add(id);
        return false;
    }
}
