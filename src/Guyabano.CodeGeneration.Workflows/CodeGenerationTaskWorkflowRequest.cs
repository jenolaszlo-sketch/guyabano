using Guyabano.CodeGeneration.Planning;

namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationTaskWorkflowRequest(
    CodeGenerationPlan Plan,
    string ParentTaskId,
    CodeGenerationLeafTask Task,
    CodeGenerationBuildCorrection? Correction = null,
    int StartingModelTier = 1,
    bool IsBuildRepair = false,
    int BuildRepairCycle = 0,
    RepositoryContextReference? RepositoryContext = null);
