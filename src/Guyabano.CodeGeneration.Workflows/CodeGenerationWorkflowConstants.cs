using Penghou.Zhinu;

namespace Guyabano.CodeGeneration.Workflows;

public static class CodeGenerationWorkflowConstants
{
    public const string WorkflowName = "guyabano-code-generation";
    public const string WorkflowVersion = "4";
    public static readonly WorkflowStepReference<
        RepositoryIndexRequest,
        RepositoryRevision> IndexRepositoryStep =
        new(new("guyabano.index-repository"));
    public static readonly WorkflowStepReference<
        RepositoryContextSelectionRequest,
        RepositoryContextSelection> SelectRepositoryContextStep =
        new(new("guyabano.select-repository-context"));
    public static readonly WorkflowStepReference<
        RepositoryContextCaptureRequest,
        RepositoryContextReference> CaptureRepositoryContextStep =
        new(new("guyabano.capture-repository-context"));
    public static readonly WorkflowStepReference<
        CodeGenerationWorkflowRequest,
        CodeGenerationWorkflowResult> PlanStep =
        new(new("guyabano.plan-code-generation"));
    public static readonly WorkflowStepReference<
        CodeGenerationDecompositionWorkflowRequest,
        CodeGenerationDecompositionWorkflowResult> DecomposeTaskStep =
        new(new("guyabano.decompose-code-generation-task"));
    public static readonly WorkflowStepReference<
        ArchitectureReviewWorkflowRequest,
        ArchitectureReviewWorkflowResult> ReviewArchitectureStep =
        new(new("guyabano.review-code-generation-architecture"));
    public static readonly WorkflowStepReference<
        ArchitectureDecisionIntegrationWorkflowRequest,
        ArchitectureDecisionIntegrationWorkflowResult> IntegrateArchitectureStep =
        new(new("guyabano.integrate-code-generation-architecture"));
    public static readonly WorkflowStepReference<
        ArchitectureGapResolutionWorkflowRequest,
        ArchitectureGapResolutionWorkflowResult> ResolveArchitectureGapStep =
        new(new("guyabano.resolve-code-generation-architecture-gap"));
    public static readonly WorkflowStepReference<
        CodeGenerationScaffoldingRequest,
        CodeGenerationScaffoldingResult> ScaffoldStep =
        new(new("guyabano.scaffold-code-generation"));
    public static readonly WorkflowStepReference<
        CodeGenerationTaskWorkflowRequest,
        CodeGenerationTaskWorkflowResult> GenerateTaskStep =
        new(new("guyabano.generate-code-task"));
    public static readonly WorkflowStepReference<
        CodeGenerationBuildRequest,
        CodeGenerationBuildResult> BuildStep =
        new(new("guyabano.build-generated-code"));
    public static readonly WorkflowStepReference<
        CodeGenerationCheckpointLoadRequest,
        CodeGenerationRunCheckpoint> LoadCheckpointStep =
        new(new("guyabano.load-code-generation-checkpoint"));
    public static readonly WorkflowStepReference<
        CodeGenerationCheckpointRequest,
        Guyabano.Artifacts.ArtifactReference> SaveCheckpointStep =
        new(new("guyabano.save-code-generation-checkpoint"));
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
