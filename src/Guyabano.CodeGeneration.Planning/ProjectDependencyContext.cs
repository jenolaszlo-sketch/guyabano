namespace Guyabano.CodeGeneration.Planning;

public sealed record ProjectDependencyContext(
    string Name,
    string Path,
    ProjectRole Role);
