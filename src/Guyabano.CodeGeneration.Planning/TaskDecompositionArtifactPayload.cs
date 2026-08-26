namespace Guyabano.CodeGeneration.Planning;

public sealed record TaskDecompositionArtifactPayload(
    string ParentTaskId,
    CodeGenerationTaskDecomposition Decomposition,
    int ArchitectureVersion = 1);
