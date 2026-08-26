using Penghou.Zhinu;

namespace Guyabano.CodeGeneration.Workflows;

public static class CodeGenerationWorkflowConstants
{
    public const string WorkflowName = "guyabano-code-generation";
    public const string WorkflowVersion = "2";
    public static readonly StepImplementationKey PlanStep =
        new("guyabano.plan-code-generation");
    public static readonly StepImplementationKey DecomposeTaskStep =
        new("guyabano.decompose-code-generation-task");
    public static readonly StepImplementationKey ReviewArchitectureStep =
        new("guyabano.review-code-generation-architecture");
    public static readonly StepImplementationKey IntegrateArchitectureStep =
        new("guyabano.integrate-code-generation-architecture");
    public static readonly StepImplementationKey ResolveArchitectureGapStep =
        new("guyabano.resolve-code-generation-architecture-gap");
    public static readonly StepImplementationKey ScaffoldStep =
        new("guyabano.scaffold-code-generation");
    public static readonly StepImplementationKey GenerateTaskStep =
        new("guyabano.generate-code-task");
    public static readonly StepImplementationKey BuildStep =
        new("guyabano.build-generated-code");
    public static readonly StepImplementationKey LoadCheckpointStep =
        new("guyabano.load-code-generation-checkpoint");
    public static readonly StepImplementationKey SaveCheckpointStep =
        new("guyabano.save-code-generation-checkpoint");
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
