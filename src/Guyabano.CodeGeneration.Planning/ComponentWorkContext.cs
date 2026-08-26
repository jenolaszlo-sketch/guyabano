namespace Guyabano.CodeGeneration.Planning;

public sealed record ComponentWorkContext(
    string PlanTitle,
    string PlanSummary,
    ProductMission Mission,
    PlannedSolution Solution,
    GenerationTaskPlan ParentTask,
    PlannedModule Module,
    PlannedProject Project,
    IReadOnlyList<ProjectDependencyContext> ProjectDependencies,
    IReadOnlyList<PlanUseCase> UseCases,
    IReadOnlyList<PlanAcceptanceCriterion> AcceptanceCriteria,
    IReadOnlyList<PlannedContract> Contracts,
    IReadOnlyList<ArchitectureDecision> Decisions,
    IReadOnlyList<ArchitectureNote> ArchitectureNotes,
    IReadOnlyList<ComponentWorkDependency> ComponentDependencies,
    ResolvedDependencyContext ResolvedDependencies);
