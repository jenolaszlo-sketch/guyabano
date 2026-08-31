namespace Guyabano.CodeGeneration.Planning;

internal static class StagedPlanningValidator
{
    private static readonly HashSet<int> AllowedComplexityPoints =
        [1, 2, 3, 5];

    public static IReadOnlyList<string> Validate(
        StagedPlanningArtifacts artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var errors = new List<string>();
        ValidateDomain(artifacts.Domain, errors);
        ValidateTopology(artifacts.Domain, artifacts.Topology, errors);
        ValidateContracts(
            artifacts.Domain,
            artifacts.Topology,
            artifacts.ContractCatalogs,
            errors);
        ValidateComponents(
            artifacts.Domain,
            artifacts.Topology,
            artifacts.ContractCatalogs,
            artifacts.ComponentManifests,
            errors);
        return errors;
    }

    internal static IReadOnlyList<string> ValidateDomain(
        DomainDiscovery domain)
    {
        var errors = new List<string>();
        ValidateDomain(domain, errors);
        return errors;
    }

    private static void ValidateDomain(
        DomainDiscovery domain,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(domain.Title) ||
            string.IsNullOrWhiteSpace(domain.Summary))
            errors.Add("Domain discovery has no title or summary.");
        if (string.IsNullOrWhiteSpace(domain.Mission.GuidingIntent) ||
            domain.Mission.SuccessOutcomes.Count == 0)
            errors.Add("Domain discovery has no guiding mission or success outcomes.");
        if (domain.Capabilities.Count == 0)
            errors.Add("Domain discovery contains no capabilities.");
        if (domain.UseCases.Count == 0)
            errors.Add("Domain discovery contains no use cases.");
        ValidateUniqueNames(
            domain.Terms.Select(item => item.Name),
            "domain term",
            errors);
        ValidateUniqueNames(
            domain.Capabilities.Select(item => item.Name),
            "domain capability",
            errors);
        ValidateUniqueNames(
            domain.UseCases.Select(item => item.Name),
            "domain use case",
            errors);
        foreach (var useCase in domain.UseCases)
        {
            if (useCase.AcceptanceCriteria.Count == 0)
                errors.Add($"Use case '{useCase.Name}' has no acceptance criteria.");
            if (string.IsNullOrWhiteSpace(useCase.Actor) ||
                string.IsNullOrWhiteSpace(useCase.Objective) ||
                useCase.Outcomes.Count == 0)
                errors.Add($"Use case '{useCase.Name}' is incomplete.");
        }

        var capabilityNames = domain.Capabilities
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        ValidateReferences(
            "Use case capability",
            domain.UseCases.Select(item => item.CapabilityName),
            capabilityNames,
            errors);
        var representedCapabilities = domain.UseCases
            .Select(item => item.CapabilityName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var capability in capabilityNames.Where(item =>
                     !representedCapabilities.Contains(item)))
            errors.Add($"Capability '{capability}' has no use case.");
        foreach (var inferredDefault in domain.InferredDefaults)
            ValidateReferences(
                $"Default '{inferredDefault.Subject}'",
                inferredDefault.AffectedCapabilities,
                capabilityNames,
                errors);
        foreach (var ambiguity in domain.ProductAmbiguities)
            ValidateReferences(
                $"Ambiguity '{ambiguity.Question}'",
                ambiguity.AffectedCapabilities,
                capabilityNames,
                errors);
    }

    internal static IReadOnlyList<string> ValidateTopology(
        DomainDiscovery domain,
        SolutionTopology topology)
    {
        var errors = new List<string>();
        ValidateTopology(domain, topology, errors);
        return errors;
    }

    private static void ValidateTopology(
        DomainDiscovery domain,
        SolutionTopology topology,
        ICollection<string> errors)
    {
        if (topology.Projects.Count == 0)
            errors.Add("Solution topology contains no projects.");
        if (topology.BoundedContexts.Count == 0)
            errors.Add("Solution topology contains no bounded contexts.");
        if (topology.Modules.Count == 0)
            errors.Add("Solution topology contains no project modules.");

        ValidateUniqueNames(
            topology.Projects.Select(item => item.Name),
            "project",
            errors);
        ValidateUniqueNames(
            topology.BoundedContexts.Select(item => item.Name),
            "bounded context",
            errors);
        ValidateUniqueNames(
            topology.Modules.Select(item => item.Name),
            "topology module",
            errors);
        ValidateUniqueNames(
            topology.Decisions.Select(item => item.Title),
            "architecture decision",
            errors);

        var projectNames = topology.Projects
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var contextNames = topology.BoundedContexts
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var capabilityNames = domain.Capabilities
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var packageNames = topology.Projects
            .SelectMany(item => item.Packages)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectByName = ToFirstDictionary(
            topology.Projects,
            item => item.Name,
            item => item,
            StringComparer.Ordinal);

        foreach (var project in topology.Projects)
        {
            if (!Enum.IsDefined(project.Role))
                errors.Add($"Project '{project.Name}' uses unsupported role '{project.Role}'.");
            ValidateReferences(
                $"Project '{project.Name}' dependency",
                project.ProjectDependencies,
                projectNames,
                errors);
            if (project.ProjectDependencies.Contains(
                    project.Name,
                    StringComparer.Ordinal))
                errors.Add($"Project '{project.Name}' references itself.");
            foreach (var dependencyName in project.ProjectDependencies)
            {
                if (!projectByName.TryGetValue(dependencyName, out var dependency))
                    continue;
                if (project.Role != ProjectRole.Test &&
                    dependency.Role == ProjectRole.Test)
                    errors.Add(
                        $"Production project '{project.Name}' must not depend on test project '{dependency.Name}'.");
                if (!IsAllowedRoleDependency(project.Role, dependency.Role))
                    errors.Add(
                        $"Project '{project.Name}' with role '{project.Role}' must not depend on project '{dependency.Name}' with role '{dependency.Role}'.");
            }
        }

        foreach (var context in topology.BoundedContexts)
        {
            ValidateReferences(
                $"Bounded context '{context.Name}'",
                context.CapabilityNames,
                capabilityNames,
                errors);
            ValidateReferences(
                $"Bounded context '{context.Name}' dependency",
                context.DependsOnContextNames,
                contextNames,
                errors);
        }

        var assignedCapabilities = topology.BoundedContexts
            .SelectMany(item => item.CapabilityNames)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var capabilityName in capabilityNames.Where(item =>
                     !assignedCapabilities.Contains(item)))
            errors.Add($"Capability '{capabilityName}' is not assigned to a bounded context.");

        foreach (var module in topology.Modules)
        {
            if (!contextNames.Contains(module.BoundedContextName))
                errors.Add($"Module '{module.Name}' references unknown bounded context '{module.BoundedContextName}'.");
            if (!projectNames.Contains(module.ProjectName))
                errors.Add($"Module '{module.Name}' references unknown project '{module.ProjectName}'.");
        }

        foreach (var decision in topology.Decisions)
        {
            ValidateReferences(
                $"Decision '{decision.Title}'",
                decision.AffectedContextNames,
                contextNames,
                errors);
            foreach (var package in decision.RelatedPackages.Where(item =>
                         !packageNames.Contains(item)))
                errors.Add(
                    $"Decision '{decision.Title}' references unknown package '{package}'.");
        }

        if (ContainsCycle(ToFirstDictionary(
                topology.Projects,
                item => item.Name,
                item => (IReadOnlyCollection<string>)item.ProjectDependencies,
                StringComparer.Ordinal)))
            errors.Add("The project dependency graph contains a cycle.");
        if (ContainsCycle(ToFirstDictionary(
                topology.BoundedContexts,
                item => item.Name,
                item => (IReadOnlyCollection<string>)item.DependsOnContextNames,
                StringComparer.Ordinal)))
            errors.Add("The bounded-context dependency graph contains a cycle.");
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

    internal static IReadOnlyList<string> ValidateContracts(
        DomainDiscovery domain,
        SolutionTopology topology,
        IReadOnlyList<BoundedContextContractCatalog> catalogs)
    {
        var errors = new List<string>();
        ValidateContracts(domain, topology, catalogs, errors);
        return errors;
    }

    private static void ValidateContracts(
        DomainDiscovery domain,
        SolutionTopology topology,
        IReadOnlyList<BoundedContextContractCatalog> catalogs,
        ICollection<string> errors)
    {
        var contextNames = topology.BoundedContexts
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var moduleByName = ToFirstDictionary(
            topology.Modules,
            item => item.Name,
            item => item,
            StringComparer.Ordinal);
        var capabilityNames = domain.Capabilities
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var packageNames = topology.Projects
            .SelectMany(item => item.Packages)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ValidateUniqueNames(
            catalogs.Select(item => item.BoundedContextName),
            "contract catalog context",
            errors);
        ValidateUniqueNames(
            catalogs.SelectMany(item => item.Contracts)
                .Select(item => item.Name),
            "contract",
            errors);

        foreach (var catalog in catalogs)
        {
            if (!contextNames.Contains(catalog.BoundedContextName))
                errors.Add($"Contract catalog references unknown bounded context '{catalog.BoundedContextName}'.");
            foreach (var contract in catalog.Contracts)
            {
                if (!moduleByName.TryGetValue(contract.ModuleName, out var module))
                    errors.Add($"Contract '{contract.Name}' references unknown module '{contract.ModuleName}'.");
                else if (!module.BoundedContextName.Equals(
                             catalog.BoundedContextName,
                             StringComparison.Ordinal))
                    errors.Add($"Contract '{contract.Name}' is assigned to a module outside bounded context '{catalog.BoundedContextName}'.");
                ValidateReferences(
                    $"Contract '{contract.Name}'",
                    contract.CapabilityNames,
                    capabilityNames,
                    errors);
            }
            ValidateStageDecisions(
                catalog.Decisions,
                contextNames,
                packageNames,
                errors);
            ValidateStageDefaults(
                catalog.InferredDefaults,
                capabilityNames,
                errors);
        }

        ValidateUniqueNames(
            topology.Decisions.Select(item => item.Title)
                .Concat(catalogs.SelectMany(item => item.Decisions)
                    .Select(item => item.Title)),
            "architecture decision",
            errors);
    }

    internal static IReadOnlyList<string> ValidateComponents(
        DomainDiscovery domain,
        SolutionTopology topology,
        IReadOnlyList<BoundedContextContractCatalog> catalogs,
        IReadOnlyList<BoundedContextComponentManifest> manifests)
    {
        var errors = new List<string>();
        ValidateComponents(
            domain,
            topology,
            catalogs,
            manifests,
            errors);
        return errors;
    }

    private static void ValidateComponents(
        DomainDiscovery domain,
        SolutionTopology topology,
        IReadOnlyList<BoundedContextContractCatalog> catalogs,
        IReadOnlyList<BoundedContextComponentManifest> manifests,
        ICollection<string> errors)
    {
        var contextNames = topology.BoundedContexts
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var moduleByName = ToFirstDictionary(
            topology.Modules,
            item => item.Name,
            item => item,
            StringComparer.Ordinal);
        var contractNames = catalogs.SelectMany(item => item.Contracts)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var capabilityNames = domain.Capabilities
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var acceptanceIdsByCapability = domain.UseCases
            .GroupBy(item => item.CapabilityName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(useCase =>
                        useCase.AcceptanceCriteria.Select(criterion =>
                            RequirementIdentity.AcceptanceCriterionId(
                                useCase,
                                criterion)))
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var acceptanceIds = acceptanceIdsByCapability.Values
            .SelectMany(item => item)
            .ToHashSet(StringComparer.Ordinal);
        var packageNames = topology.Projects
            .SelectMany(item => item.Packages)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var components = manifests.SelectMany(item => item.Components)
            .ToArray();
        var componentNames = components.Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var projectByName = ToFirstDictionary(
            topology.Projects,
            item => item.Name,
            item => item,
            StringComparer.Ordinal);
        var contractByName = ToFirstDictionary(
            catalogs.SelectMany(item => item.Contracts),
            item => item.Name,
            item => item,
            StringComparer.Ordinal);

        ValidateUniqueNames(
            manifests.Select(item => item.BoundedContextName),
            "component manifest context",
            errors);
        ValidateUniqueNames(
            components.Select(item => item.Name),
            "component",
            errors);

        foreach (var manifest in manifests)
        {
            if (!contextNames.Contains(manifest.BoundedContextName))
                errors.Add($"Component manifest references unknown bounded context '{manifest.BoundedContextName}'.");
            foreach (var component in manifest.Components)
            {
                if (!moduleByName.TryGetValue(component.ModuleName, out var module))
                    errors.Add($"Component '{component.Name}' references unknown module '{component.ModuleName}'.");
                else
                {
                    if (!module.BoundedContextName.Equals(
                            manifest.BoundedContextName,
                            StringComparison.Ordinal))
                        errors.Add($"Component '{component.Name}' is assigned outside bounded context '{manifest.BoundedContextName}'.");
                    if (!module.ProjectName.Equals(
                            component.ProjectName,
                            StringComparison.Ordinal))
                        errors.Add($"Component '{component.Name}' project does not match module '{component.ModuleName}'.");
                }

                ValidateReferences(
                    $"Component '{component.Name}' contract",
                    component.DefinesContractNames.Concat(
                        component.ImplementsPortNames).Concat(
                        component.ConsumesContractNames),
                    contractNames,
                    errors);
                ValidateReferences(
                    $"Component '{component.Name}' dependency",
                    ComponentDependencies(component),
                    componentNames,
                    errors);
                ValidateReferences(
                    $"Component '{component.Name}' capability",
                    component.CapabilityNames,
                    capabilityNames,
                    errors);
                ValidateReferences(
                    $"Component '{component.Name}' acceptance criterion",
                    component.AcceptanceCriterionIds,
                    acceptanceIds,
                    errors);
                if (component.Files.Count == 0)
                    errors.Add($"Component '{component.Name}' has no files.");
                if (!AllowedComplexityPoints.Contains(component.ComplexityPoints))
                    errors.Add($"Component '{component.Name}' uses unsupported complexity points '{component.ComplexityPoints}'.");

                ValidateTypedRelationships(
                    component,
                    moduleByName,
                    projectByName,
                    contractByName,
                    components,
                    errors);
            }
            ValidateStageDecisions(
                manifest.Decisions,
                contextNames,
                packageNames,
                errors);
            ValidateStageDefaults(
                manifest.InferredDefaults,
                capabilityNames,
                errors);

            var topologyContext = topology.BoundedContexts
                .FirstOrDefault(context => context.Name.Equals(
                    manifest.BoundedContextName,
                    StringComparison.Ordinal));
            if (topologyContext is null)
                continue;
            var contextCapabilities = topologyContext.CapabilityNames;
            var expectedCriteria = contextCapabilities
                .Where(acceptanceIdsByCapability.ContainsKey)
                .SelectMany(capability =>
                    acceptanceIdsByCapability[capability])
                .ToHashSet(StringComparer.Ordinal);
            var assignedCriteria = manifest.Components
                .SelectMany(component => component.AcceptanceCriterionIds)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var criterionId in expectedCriteria.Where(id =>
                         !assignedCriteria.Contains(id)))
                errors.Add($"Bounded context '{manifest.BoundedContextName}' acceptance criterion '{criterionId}' has no owning component.");
        }

        foreach (var contract in contractByName.Values)
        {
            var definers = components.Where(component =>
                    component.DefinesContractNames.Contains(
                        contract.Name,
                        StringComparer.Ordinal))
                .Select(component => component.Name)
                .ToArray();
            if (definers.Length == 0)
                errors.Add($"Contract '{contract.Name}' has no defining component.");
            else if (definers.Length > 1)
                errors.Add(
                    $"Contract '{contract.Name}' is defined by multiple components: {string.Join(", ", definers)}.");
        }

        var componentCycle = FindComponentDependencyCycle(components);
        if (componentCycle.Count > 0)
        {
            errors.Add(
                $"The component dependency graph contains a cycle: {string.Join(" -> ", componentCycle)}.");
        }

        ValidateUniqueNames(
            topology.Decisions.Select(item => item.Title)
                .Concat(catalogs.SelectMany(item => item.Decisions)
                    .Select(item => item.Title))
                .Concat(manifests.SelectMany(item => item.Decisions)
                    .Select(item => item.Title)),
            "architecture decision",
            errors);
    }

    private static void ValidateTypedRelationships(
        StagedComponent component,
        IReadOnlyDictionary<string, TopologyModulePlan> moduleByName,
        IReadOnlyDictionary<string, PlannedProject> projectByName,
        IReadOnlyDictionary<string, StagedContract> contractByName,
        IReadOnlyCollection<StagedComponent> components,
        ICollection<string> errors)
    {
        if (!projectByName.TryGetValue(component.ProjectName, out var project))
            return;

        foreach (var contractName in component.DefinesContractNames)
        {
            if (!contractByName.TryGetValue(contractName, out var contract) ||
                !moduleByName.TryGetValue(contract.ModuleName, out var contractModule))
                continue;
            if (!contractModule.ProjectName.Equals(
                    component.ProjectName,
                    StringComparison.Ordinal))
                errors.Add(
                    $"Component '{component.Name}' defines contract '{contractName}' outside its owning project '{contractModule.ProjectName}'.");
        }

        foreach (var contractName in component.ImplementsPortNames)
        {
            if (contractByName.TryGetValue(contractName, out var contract) &&
                !contract.Kind.Contains("interface", StringComparison.OrdinalIgnoreCase))
                errors.Add(
                    $"Component '{component.Name}' implements '{contractName}', but that contract is not an interface port.");
        }

        if (component.RegistersImplementationNames.Count > 0 &&
            project.Role != ProjectRole.CompositionRoot)
            errors.Add(
                $"Component '{component.Name}' registers implementations outside a CompositionRoot project.");
        if (component.TestsComponentNames.Count > 0 && project.Role != ProjectRole.Test)
            errors.Add(
                $"Component '{component.Name}' tests components outside a Test project.");

        var duplicateTargets = component.UsesConcreteComponentNames
            .Concat(component.RegistersImplementationNames)
            .Concat(component.TestsComponentNames)
            .GroupBy(item => item, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var target in duplicateTargets)
            errors.Add(
                $"Component '{component.Name}' assigns multiple relationship types to component '{target}'.");

        var componentByName = ToFirstDictionary(
            components,
            item => item.Name,
            item => item,
            StringComparer.Ordinal);
        foreach (var targetName in ComponentDependencies(component))
        {
            if (!componentByName.TryGetValue(targetName, out var target) ||
                !projectByName.TryGetValue(target.ProjectName, out var targetProject))
                continue;
            if (project.Role != ProjectRole.Test && targetProject.Role == ProjectRole.Test)
                errors.Add(
                    $"Production component '{component.Name}' depends on test component '{targetName}'.");
        }
    }

    private static IEnumerable<string> ComponentDependencies(
        StagedComponent component) =>
        component.UsesConcreteComponentNames
            .Concat(component.RegistersImplementationNames)
            .Concat(component.TestsComponentNames);

    private static IReadOnlyList<string> FindComponentDependencyCycle(
        IReadOnlyList<StagedComponent> components)
    {
        var known = components.Select(component => component.Name)
            .ToHashSet(StringComparer.Ordinal);
        var dependencies = ToFirstDictionary(
            components,
            component => component.Name,
            component => ComponentDependencies(component)
                .Where(known.Contains)
                .ToArray(),
            StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();

        foreach (var name in dependencies.Keys.OrderBy(
                     item => item,
                     StringComparer.Ordinal))
        {
            var cycle = VisitComponent(name, dependencies, state, stack);
            if (cycle.Count > 0)
                return cycle;
        }

        return [];
    }

    private static IReadOnlyList<string> VisitComponent(
        string name,
        IReadOnlyDictionary<string, string[]> dependencies,
        IDictionary<string, int> state,
        IList<string> stack)
    {
        if (state.TryGetValue(name, out var currentState))
        {
            if (currentState != 1)
                return [];
            var start = stack.IndexOf(name);
            return stack.Skip(start).Append(name).ToArray();
        }

        state[name] = 1;
        stack.Add(name);
        foreach (var dependency in dependencies[name])
        {
            var cycle = VisitComponent(
                dependency,
                dependencies,
                state,
                stack);
            if (cycle.Count > 0)
                return cycle;
        }
        stack.RemoveAt(stack.Count - 1);
        state[name] = 2;
        return [];
    }

    private static void ValidateStageDecisions(
        IEnumerable<StagedArchitectureDecision> decisions,
        IReadOnlySet<string> contextNames,
        IReadOnlySet<string> packageNames,
        ICollection<string> errors)
    {
        foreach (var decision in decisions)
        {
            ValidateReferences(
                $"Decision '{decision.Title}' context",
                decision.AffectedContextNames,
                contextNames,
                errors);
            foreach (var package in decision.RelatedPackages.Where(item =>
                         !packageNames.Contains(item)))
                errors.Add(
                    $"Decision '{decision.Title}' references unknown package '{package}'.");
        }
    }

    private static Dictionary<string, TValue> ToFirstDictionary<TSource, TValue>(
        IEnumerable<TSource> source,
        Func<TSource, string> keySelector,
        Func<TSource, TValue> valueSelector,
        StringComparer comparer) =>
        source.GroupBy(keySelector, comparer)
            .ToDictionary(
                group => group.Key,
                group => valueSelector(group.First()),
                comparer);

    private static void ValidateStageDefaults(
        IEnumerable<DiscoveredDomainDefault> defaults,
        IReadOnlySet<string> capabilityNames,
        ICollection<string> errors)
    {
        foreach (var inferredDefault in defaults)
        {
            if (string.IsNullOrWhiteSpace(inferredDefault.Subject) ||
                string.IsNullOrWhiteSpace(inferredDefault.Decision) ||
                inferredDefault.Reasons.Count == 0)
                errors.Add("An inferred default is incomplete.");
            ValidateReferences(
                $"Default '{inferredDefault.Subject}' capability",
                inferredDefault.AffectedCapabilities,
                capabilityNames,
                errors);
        }
    }

    private static void ValidateUniqueNames(
        IEnumerable<string> names,
        string kind,
        ICollection<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                errors.Add($"A {kind} has an empty name.");
            else if (!seen.Add(name))
                errors.Add($"Duplicate {kind} name '{name}'.");
        }
    }

    private static void ValidateReferences(
        string owner,
        IEnumerable<string> references,
        IReadOnlySet<string> known,
        ICollection<string> errors)
    {
        foreach (var reference in references.Where(item =>
                     !known.Contains(item)))
            errors.Add($"{owner} references unknown name '{reference}'.");
    }

    private static bool ContainsCycle(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> graph)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        return graph.Keys.Any(node => Visit(node, graph, visited, visiting));
    }

    private static bool Visit(
        string node,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> graph,
        ISet<string> visited,
        ISet<string> visiting)
    {
        if (visited.Contains(node))
            return false;
        if (!visiting.Add(node))
            return true;
        if (graph.TryGetValue(node, out var dependencies))
        {
            foreach (var dependency in dependencies.Where(graph.ContainsKey))
            {
                if (Visit(dependency, graph, visited, visiting))
                    return true;
            }
        }
        visiting.Remove(node);
        visited.Add(node);
        return false;
    }
}
