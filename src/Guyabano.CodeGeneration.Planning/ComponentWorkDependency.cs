namespace Guyabano.CodeGeneration.Planning;

public sealed record ComponentWorkDependency(
    string TaskId,
    string Title,
    string ProjectName,
    ProjectRole ProjectRole,
    ComponentDependencyKind Kind,
    IReadOnlyList<string> Deliverables);
