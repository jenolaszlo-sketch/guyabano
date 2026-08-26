namespace Guyabano.CodeGeneration.Planning;

internal static class StagedCodeGenerationPlanAssembler
{
    private const string ScaffoldingTaskId = "TASK-SCAFFOLD";

    public static CodeGenerationPlan Assemble(
        StagedPlanningArtifacts artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var stageErrors = StagedPlanningValidator.Validate(artifacts);
        if (stageErrors.Count > 0)
            throw new InvalidOperationException(
                $"Staged planning artifacts are invalid: {string.Join(" ", stageErrors)}");

        var moduleIdByName = artifacts.Topology.Modules.ToDictionary(
            item => item.Name,
            item => StablePlanningId.Create("MOD", item.Name),
            StringComparer.Ordinal);
        var modules = artifacts.Topology.Modules.Select(item =>
                new PlannedModule
                {
                    Id = moduleIdByName[item.Name],
                    Name = item.Name,
                    BoundedContext = item.BoundedContextName,
                    ProjectName = item.ProjectName,
                    Responsibilities = [.. item.Responsibilities]
                })
            .ToList();

        var stagedContracts = artifacts.ContractCatalogs
            .SelectMany(catalog => catalog.Contracts.Select(contract =>
                (Catalog: catalog, Contract: contract)))
            .ToArray();
        var contractIdByName = stagedContracts.ToDictionary(
            item => item.Contract.Name,
            item => StablePlanningId.Create(
                "CONTRACT",
                item.Contract.Name),
            StringComparer.Ordinal);
        var contracts = stagedContracts.Select(item =>
                new PlannedContract
                {
                    Id = contractIdByName[item.Contract.Name],
                    Name = item.Contract.Name,
                    Kind = item.Contract.Kind,
                    ModuleId = moduleIdByName[item.Contract.ModuleName],
                    Purpose = item.Contract.Purpose,
                    Members = [.. item.Contract.Members]
                })
            .ToList();

        var stagedDecisions = artifacts.Topology.Decisions
            .Concat(artifacts.ContractCatalogs.SelectMany(item => item.Decisions))
            .Concat(artifacts.ComponentManifests.SelectMany(item => item.Decisions))
            .ToArray();
        var decisionIdByTitle = stagedDecisions.ToDictionary(
            item => item.Title,
            item => StablePlanningId.Create("ADR", item.Title),
            StringComparer.Ordinal);
        var decisions = stagedDecisions.Select(item =>
                new ArchitectureDecision
                {
                    Id = decisionIdByTitle[item.Title],
                    Title = item.Title,
                    Decision = item.Decision,
                    Reasons = [.. item.Reasons],
                    AlternativesRejected = [.. item.AlternativesRejected],
                    RelatedPackages = [.. item.RelatedPackages]
                })
            .ToList();

        var contextByCapability = artifacts.Topology.BoundedContexts
            .SelectMany(context => context.CapabilityNames.Select(capability =>
                (Capability: capability, Context: context.Name)))
            .ToDictionary(item => item.Capability, item => item.Context,
                StringComparer.Ordinal);
        var acceptance = CreateAcceptanceCriteria(
            artifacts.Domain,
            contextByCapability,
            out var acceptanceIdsByCapability,
            out var acceptanceIdsByUseCase);
        var useCases = CreateUseCases(
            artifacts.Domain,
            contextByCapability,
            acceptanceIdsByUseCase);
        var stagedComponents = artifacts.ComponentManifests
            .SelectMany(manifest => manifest.Components.Select(component =>
                (Manifest: manifest, Component: component)))
            .ToArray();
        var taskIdByComponent = stagedComponents.ToDictionary(
            item => item.Component.Name,
            item => StablePlanningId.Create("TASK", item.Component.Name),
            StringComparer.Ordinal);
        var componentsByName = stagedComponents.ToDictionary(
            item => item.Component.Name,
            item => item.Component,
            StringComparer.Ordinal);
        var tasks = new List<GenerationTaskPlan>
        {
            CreateScaffoldingTask(artifacts.Topology)
        };
        tasks.AddRange(stagedComponents.Select(item => CreateTask(
            item.Manifest,
            item.Component,
            taskIdByComponent,
            contractIdByName,
            decisionIdByTitle,
            stagedDecisions,
            moduleIdByName)));

        var notes = CreateArchitectureNotes(
            artifacts,
            acceptanceIdsByCapability,
            stagedContracts,
            stagedComponents,
            contractIdByName,
            taskIdByComponent,
            moduleIdByName);

        var plan = new CodeGenerationPlan
        {
            Mission = artifacts.Domain.Mission,
            Title = artifacts.Domain.Title,
            Summary = artifacts.Domain.Summary,
            Assumptions = [.. artifacts.Domain.Assumptions],
            Solution = artifacts.Topology.Solution,
            Projects = CreateReconciledProjects(
                artifacts.Topology,
                stagedContracts,
                stagedComponents,
                componentsByName),
            Modules = modules,
            Contracts = contracts,
            Decisions = decisions,
            ArchitectureNotes = notes,
            UseCases = useCases,
            AcceptanceCriteria = acceptance,
            Tasks = tasks
        };
        var planErrors = CodeGenerationPlanValidator.Validate(plan);
        if (planErrors.Count > 0)
            throw new InvalidOperationException(
                $"Staged planning assembly produced an invalid plan: {string.Join(" ", planErrors)}");
        return plan;
    }

    private static List<PlanAcceptanceCriterion> CreateAcceptanceCriteria(
        DomainDiscovery domain,
        IReadOnlyDictionary<string, string> contextByCapability,
        out IReadOnlyDictionary<string, IReadOnlyList<string>> idsByCapability,
        out IReadOnlyDictionary<string, IReadOnlyList<string>> idsByUseCase)
    {
        var result = new List<PlanAcceptanceCriterion>();
        var byCapability = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.Ordinal);
        var byUseCase = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.Ordinal);
        foreach (var capability in domain.Capabilities)
            byCapability[capability.Name] = [];
        foreach (var useCase in domain.UseCases)
        {
            var ids = new List<string>();
            var useCaseId = RequirementIdentity.UseCaseId(useCase);
            foreach (var criterion in useCase.AcceptanceCriteria)
            {
                var id = RequirementIdentity.AcceptanceCriterionId(
                    useCase,
                    criterion);
                ids.Add(id);
                result.Add(new PlanAcceptanceCriterion
                {
                    Id = id,
                    UseCaseId = useCaseId,
                    BoundedContext = contextByCapability[
                        useCase.CapabilityName],
                    Feature = useCase.CapabilityName,
                    Scenario = criterion.Scenario,
                    Given = [.. criterion.Given],
                    When = [.. criterion.When],
                    Then = [.. criterion.Then],
                    VerificationKinds = [.. criterion.VerificationKinds]
                });
            }
            byUseCase[useCaseId] = ids;
            byCapability[useCase.CapabilityName] = byCapability[
                    useCase.CapabilityName]
                .Concat(ids)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        idsByCapability = byCapability;
        idsByUseCase = byUseCase;
        return result;
    }

    private static List<PlanUseCase> CreateUseCases(
        DomainDiscovery domain,
        IReadOnlyDictionary<string, string> contextByCapability,
        IReadOnlyDictionary<string, IReadOnlyList<string>> acceptanceIds) =>
        domain.UseCases.Select(useCase =>
        {
            var id = RequirementIdentity.UseCaseId(useCase);
            return new PlanUseCase
            {
                Id = id,
                Name = useCase.Name,
                Capability = useCase.CapabilityName,
                BoundedContext = contextByCapability[useCase.CapabilityName],
                Actor = useCase.Actor,
                Objective = useCase.Objective,
                Preconditions = [.. useCase.Preconditions],
                Inputs = [.. useCase.Inputs],
                BusinessRules = [.. useCase.BusinessRules],
                Outcomes = [.. useCase.Outcomes],
                ErrorOutcomes = [.. useCase.ErrorOutcomes],
                AcceptanceCriterionIds = [.. acceptanceIds[id]]
            };
        }).ToList();

    private static GenerationTaskPlan CreateScaffoldingTask(
        SolutionTopology topology) =>
        new()
        {
            Id = ScaffoldingTaskId,
            Title = "Create solution scaffolding",
            Objective = "Create the declared solution, projects, references, and package requirements using deterministic tooling.",
            ExecutionKind = PlanTaskExecutionKind.Scaffolding,
            ModuleId = null,
            BoundedContext = null,
            ComplexityPoints = 1,
            ComplexityReasons = ["Solution scaffolding is deterministic."],
            DecompositionRecommended = false,
            EstimatedFiles = topology.Projects.Count + 1,
            DependsOn = [],
            ContractIds = [],
            Relationships = ComponentRelationshipPlan.Empty,
            DecisionIds = [],
            AcceptanceCriterionIds = [],
            Deliverables =
            [
                topology.Solution.Path,
                .. topology.Projects.Select(item => item.Path)
            ],
            VerificationKinds = ["Compilation"]
        };

    private static GenerationTaskPlan CreateTask(
        BoundedContextComponentManifest manifest,
        StagedComponent component,
        IReadOnlyDictionary<string, string> taskIdByComponent,
        IReadOnlyDictionary<string, string> contractIdByName,
        IReadOnlyDictionary<string, string> decisionIdByTitle,
        IReadOnlyList<StagedArchitectureDecision> decisions,
        IReadOnlyDictionary<string, string> moduleIdByName)
    {
        var dependencies = component.UsesConcreteComponentNames
            .Concat(component.RegistersImplementationNames)
            .Concat(component.TestsComponentNames)
            .Select(name => taskIdByComponent[name])
            .ToHashSet(StringComparer.Ordinal);
        dependencies.Add(ScaffoldingTaskId);

        var relatedDecisionIds = decisions
            .Where(decision => decision.AffectedContextNames.Count == 0 ||
                decision.AffectedContextNames.Contains(
                    manifest.BoundedContextName,
                    StringComparer.Ordinal))
            .Select(decision => decisionIdByTitle[decision.Title])
            .ToList();
        var criterionIds = component.AcceptanceCriterionIds
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var contractIds = component.DefinesContractNames
            .Concat(component.ImplementsPortNames)
            .Concat(component.ConsumesContractNames)
            .Select(name => contractIdByName[name])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new GenerationTaskPlan
        {
            Id = taskIdByComponent[component.Name],
            Title = $"Implement {component.Name}",
            Objective = string.Join(" ", component.Responsibilities),
            ExecutionKind = PlanTaskExecutionKind.CodeGeneration,
            ModuleId = moduleIdByName[component.ModuleName],
            BoundedContext = manifest.BoundedContextName,
            ComplexityPoints = component.ComplexityPoints,
            ComplexityReasons =
            [
                $"Component role: {component.Kind}.",
                $"Expected file count: {component.Files.Count}."
            ],
            DecompositionRecommended = component.ComplexityPoints >= 5 ||
                component.Files.Count > 2,
            EstimatedFiles = component.Files.Count,
            DependsOn = dependencies.ToList(),
            ContractIds = contractIds,
            Relationships = new ComponentRelationshipPlan
            {
                DefinesContractIds = component.DefinesContractNames
                    .Select(name => contractIdByName[name])
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                ImplementsPortContractIds = component.ImplementsPortNames
                    .Select(name => contractIdByName[name])
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                ConsumesContractIds = component.ConsumesContractNames
                    .Select(name => contractIdByName[name])
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                UsesConcreteTaskIds = component.UsesConcreteComponentNames
                    .Select(name => taskIdByComponent[name])
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                RegistersImplementationTaskIds =
                    component.RegistersImplementationNames
                        .Select(name => taskIdByComponent[name])
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                TestsTaskIds = component.TestsComponentNames
                    .Select(name => taskIdByComponent[name])
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            },
            DecisionIds = relatedDecisionIds,
            AcceptanceCriterionIds = criterionIds,
            Deliverables = [.. component.Files],
            VerificationKinds = [.. component.VerificationKinds]
        };
    }

    private static List<PlannedProject> CreateReconciledProjects(
        SolutionTopology topology,
        IReadOnlyList<(
            BoundedContextContractCatalog Catalog,
            StagedContract Contract)> stagedContracts,
        IReadOnlyList<(
            BoundedContextComponentManifest Manifest,
            StagedComponent Component)> stagedComponents,
        IReadOnlyDictionary<string, StagedComponent> componentsByName)
    {
        var projectByModule = topology.Modules.ToDictionary(
            module => module.Name,
            module => module.ProjectName,
            StringComparer.Ordinal);
        var contractProject = stagedContracts.ToDictionary(
            item => item.Contract.Name,
            item => projectByModule[item.Contract.ModuleName],
            StringComparer.Ordinal);
        var dependencies = topology.Projects.ToDictionary(
            project => project.Name,
            project => project.ProjectDependencies.ToHashSet(
                StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var (_, component) in stagedComponents)
        {
            foreach (var contractName in component.ConsumesContractNames
                         .Concat(component.ImplementsPortNames)
                         .Distinct(StringComparer.Ordinal))
            {
                var ownerProject = contractProject[contractName];
                if (!ownerProject.Equals(
                        component.ProjectName,
                        StringComparison.Ordinal))
                    dependencies[component.ProjectName].Add(ownerProject);
            }

            foreach (var dependencyName in component.UsesConcreteComponentNames
                         .Concat(component.RegistersImplementationNames)
                         .Concat(component.TestsComponentNames)
                         .Distinct(StringComparer.Ordinal))
            {
                var dependency = componentsByName[dependencyName];
                if (dependency.ProjectName.Equals(
                        component.ProjectName,
                        StringComparison.Ordinal))
                    continue;
                dependencies[component.ProjectName].Add(
                    dependency.ProjectName);
            }
        }

        return topology.Projects.Select(project => new PlannedProject
        {
            Name = project.Name,
            Path = project.Path,
            Kind = project.Kind,
            Role = project.Role,
            TargetFramework = project.TargetFramework,
            Responsibilities = [.. project.Responsibilities],
            ProjectDependencies = dependencies[project.Name]
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToList(),
            Packages = [.. project.Packages]
        }).ToList();
    }

    private static List<ArchitectureNote> CreateArchitectureNotes(
        StagedPlanningArtifacts artifacts,
        IReadOnlyDictionary<string, IReadOnlyList<string>> acceptanceIds,
        IReadOnlyList<(
            BoundedContextContractCatalog Catalog,
            StagedContract Contract)> stagedContracts,
        IReadOnlyList<(
            BoundedContextComponentManifest Manifest,
            StagedComponent Component)> stagedComponents,
        IReadOnlyDictionary<string, string> contractIds,
        IReadOnlyDictionary<string, string> taskIds,
        IReadOnlyDictionary<string, string> moduleIds)
    {
        var contextByCapability = artifacts.Topology.BoundedContexts
            .SelectMany(context => context.CapabilityNames.Select(name =>
                (CapabilityName: name, Context: context)))
            .GroupBy(item => item.CapabilityName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Context.Name).ToArray(),
                StringComparer.Ordinal);
        var moduleIdsByContext = artifacts.Topology.Modules
            .GroupBy(item => item.BoundedContextName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => moduleIds[item.Name]).ToArray(),
                StringComparer.Ordinal);

        var inferredDefaults = artifacts.Domain.InferredDefaults
            .Select(item => (Source: "domain", Default: item))
            .Concat(artifacts.ContractCatalogs.SelectMany(catalog =>
                catalog.InferredDefaults.Select(item => (
                    Source: $"contracts-{catalog.BoundedContextName}",
                    Default: item))))
            .Concat(artifacts.ComponentManifests.SelectMany(manifest =>
                manifest.InferredDefaults.Select(item => (
                    Source: $"components-{manifest.BoundedContextName}",
                    Default: item))))
            .ToArray();

        return inferredDefaults.Select(entry =>
        {
            var inferredDefault = entry.Default;
            var affected = inferredDefault.AffectedCapabilities
                .SelectMany(name => acceptanceIds[name])
                .Concat(stagedContracts
                    .Where(item => item.Contract.CapabilityNames.Intersect(
                        inferredDefault.AffectedCapabilities,
                        StringComparer.Ordinal).Any())
                    .Select(item => contractIds[item.Contract.Name]))
                .Concat(stagedComponents
                    .Where(item => item.Component.CapabilityNames.Intersect(
                        inferredDefault.AffectedCapabilities,
                        StringComparer.Ordinal).Any())
                    .Select(item => taskIds[item.Component.Name]))
                .Concat(inferredDefault.AffectedCapabilities
                    .SelectMany(name => contextByCapability[name])
                    .SelectMany(name => moduleIdsByContext[name]))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return new ArchitectureNote
            {
                Id = StablePlanningId.Create(
                    "NOTE",
                    $"{entry.Source}-{inferredDefault.Subject}"),
                Category = NormalizeCategory(inferredDefault.Kind),
                Subject = inferredDefault.Subject,
                MissingInformation = inferredDefault.MissingInformation,
                Decision = inferredDefault.Decision,
                Reasons = [.. inferredDefault.Reasons],
                Impact = inferredDefault.Impact,
                AffectedIds = affected,
                UserOverridable = inferredDefault.UserOverridable
            };
        }).ToList();
    }

    internal static ArchitectureNoteCategory NormalizeCategory(string value)
    {
        var normalized = new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        if (normalized.Contains("domain") &&
            normalized.Contains("constraint"))
            return ArchitectureNoteCategory.InferredDomainConstraint;
        if (normalized.Contains("platform") ||
            normalized.Contains("framework") ||
            normalized.Contains("convention"))
            return ArchitectureNoteCategory.PlatformConvention;
        if (normalized.Contains("best") && normalized.Contains("practice"))
            return ArchitectureNoteCategory.BestPractice;
        if (normalized.Contains("technical"))
            return ArchitectureNoteCategory.TechnicalChoice;
        if (normalized.Contains("defer"))
            return ArchitectureNoteCategory.DeferredDecision;
        if (normalized.Contains("clarification") ||
            normalized.Contains("userrequired"))
            return ArchitectureNoteCategory.UserClarificationRequired;
        return ArchitectureNoteCategory.InferredDefault;
    }
}
