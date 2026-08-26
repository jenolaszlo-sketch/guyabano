namespace Guyabano.CodeGeneration.Planning;

public interface IResolvedDependencyContextBuilder
{
    ResolvedDependencyContext Build(
        CodeGenerationPlan plan,
        string targetTaskId,
        IReadOnlyCollection<TaskDecompositionArtifactPayload>
            upstreamDecompositions);
}
