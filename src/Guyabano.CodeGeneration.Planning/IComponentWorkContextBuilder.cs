namespace Guyabano.CodeGeneration.Planning;

public interface IComponentWorkContextBuilder
{
    ComponentWorkContext Build(
        CodeGenerationPlan plan,
        string parentTaskId,
        ResolvedDependencyContext resolvedDependencies);
}
