namespace Guyabano.CodeGeneration.Workflows;

public static class CodeGenerationWorkflowConstants
{
    public const string WorkflowName = "guyabano-code-generation";
    public const string WorkflowVersion = "1";
    public const string PlanActivity = "PlanCodeGeneration";
    public const string DecomposeTaskActivity =
        "DecomposeCodeGenerationTask";
    public const string ReviewArchitectureActivity =
        "ReviewCodeGenerationArchitecture";
    public const string IntegrateArchitectureDecisionsActivity =
        "AmendCodeGenerationArchitecture";
    public const string ResolveArchitectureGapActivity =
        "ResolveCodeGenerationArchitectureGap";
    public const string ScaffoldActivity = "ScaffoldCodeGeneration";
    public const string GenerateTaskActivity = "GenerateCodeTask";
    public const string BuildActivity = "BuildGeneratedCode";
    public const string LoadCheckpointActivity =
        "LoadCodeGenerationCheckpoint";
    public const string SaveCheckpointActivity =
        "SaveCodeGenerationCheckpoint";
    public const int MaximumBuildRepairCycles = 5;
    public const int MaximumBuildAttempts =
        MaximumBuildRepairCycles + 1;
    public const int MaximumAttemptsPerModel = 2;
    public const int MaximumModelTiers = 2;
    public const int MaximumGenerateAttempts =
        MaximumAttemptsPerModel * MaximumModelTiers;
    public const int MaximumPlanningTransportAttempts = 2;
    public const int MaximumDecompositionModelTiers = 2;
    public const int MaximumDecompositionAttempts =
        MaximumAttemptsPerModel * MaximumDecompositionModelTiers;
    public const int MaximumArchitectureReviewPasses = 5;
    public const int MaximumDecompositionArchitectureIntegrations = 2;
    public const int MaximumArchitectureTransportAttempts = 2;
    public const int MaximumArchitectureModelOutputAttempts = 3;
    public const int MaximumArchitectureModelQualityAttempts = 3;
}
