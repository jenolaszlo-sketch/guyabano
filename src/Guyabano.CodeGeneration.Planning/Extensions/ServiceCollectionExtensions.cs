using Microsoft.Extensions.DependencyInjection;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeGenerationPlanning(
        this IServiceCollection services)
    {
        services.AddSingleton<
            IPromptBuilder<CodeGenerationPlanningPromptContext>,
            CodeGenerationPlanningPromptBuilder>();
        services.AddSingleton<
            IPromptBuilder<DomainDiscoveryPromptContext>,
            DomainDiscoveryPromptBuilder>();
        services.AddSingleton<
            IPromptBuilder<SolutionTopologyPromptContext>,
            SolutionTopologyPromptBuilder>();
        services.AddSingleton<
            IPromptBuilder<ContractDesignPromptContext>,
            ContractDesignPromptBuilder>();
        services.AddSingleton<
            IPromptBuilder<ComponentDesignPromptContext>,
            ComponentDesignPromptBuilder>();
        services.AddSingleton<
            ICodeGenerationPlanParser,
            CodeGenerationPlanParser>();
        services.AddSingleton<
            ICodeGenerationPlanningService,
            CodeGenerationPlanningService>();
        services.AddSingleton<
            IPromptBuilder<CodeGenerationDecompositionPromptContext>,
            CodeGenerationDecompositionPromptBuilder>();
        services.AddSingleton<
            ICodeGenerationTaskDecompositionParser,
            CodeGenerationTaskDecompositionParser>();
        services.AddSingleton<
            ICodeGenerationTaskDecompositionService,
            CodeGenerationTaskDecompositionService>();
        services.AddSingleton<
            IResolvedDependencyContextBuilder,
            ResolvedDependencyContextBuilder>();
        services.AddSingleton<
            IComponentWorkContextBuilder,
            ComponentWorkContextBuilder>();
        services.AddSingleton<
            IPromptBuilder<ArchitectureReviewPromptContext>,
            ArchitectureReviewPromptBuilder>();
        services.AddSingleton<ArchitectureReviewParser>();
        services.AddSingleton<IArchitectureReviewService,
            ArchitectureReviewService>();
        services.AddSingleton<
            IPromptBuilder<ArchitectureDecisionIntegrationPromptContext>,
            ArchitectureDecisionIntegrationPromptBuilder>();
        services.AddSingleton<ArchitectureDecisionPatchParser>();
        services.AddSingleton<IArchitectureDecisionIntegrator,
            ArchitectureDecisionIntegrator>();
        services.AddSingleton<
            IPromptBuilder<ArchitectureGapResolutionPromptContext>,
            ArchitectureGapResolutionPromptBuilder>();
        services.AddSingleton<IArchitectureGapResolutionService,
            ArchitectureGapResolutionService>();
        services.AddSingleton<IArchitecturePracticeProvider,
            DefaultArchitecturePracticeProvider>();
        services.AddSingleton<
            IPromptBuilder<PlanningGapResolutionPromptContext>,
            PlanningGapResolutionPromptBuilder>();

        return services;
    }
}
