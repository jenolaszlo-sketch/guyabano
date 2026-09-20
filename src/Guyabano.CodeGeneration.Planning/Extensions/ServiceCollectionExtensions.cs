using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Fuwen.Compiler;
using Penghou.Guihua;
using Penghou.Guihua.Baize;
using Guyabano.CodeGeneration.Planning.Fuwen;
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
        services.AddSingleton<
            IPromptBuilder<WorkflowAuthoringPromptContext>,
            WorkflowAuthoringPromptBuilder>();
        services.AddSingleton<WorkflowAuthor>(provider =>
            new WorkflowAuthor(
                provider.GetRequiredService<ILlmRouter>(),
                provider.GetRequiredService<IPromptBuilder<WorkflowAuthoringPromptContext>>(),
                provider.GetRequiredService<ITrustedCatalogue>()));

        return services;
    }

    /// <summary>
    /// Registers the Fuwen-hosted planning stages: per-stage inference
    /// executors plus the fan-out bundle activity, configured from the
    /// existing <c>CodeGeneration</c> section (same keys as
    /// <c>CodeGenerationWorkerOptions</c>). Requires the Baize router
    /// (<c>AddLlmRouting</c>), tools (<c>AddLlmTools</c>), prompting
    /// (<c>AddLlmPrompting</c>), and <c>AddCodeGenerationPlanning</c>.
    /// </summary>
    public static IServiceCollection AddFuwenPlanning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FuwenPlanningOptions>()
            .Bind(configuration.GetSection("CodeGeneration"))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PlannerModel),
                "CodeGeneration:PlannerModel is required.")
            .Validate(
                options => options.PlannerMaxTokens > 0 &&
                    options.DomainMaxTokens > 0 &&
                    options.TopologyMaxTokens > 0 &&
                    options.ContractMaxTokens > 0 &&
                    options.ComponentMaxTokens > 0,
                "All Fuwen planning token budgets must be positive.")
            .Validate(
                options => options.RepositoryContextMaximumPromptCharacters > 0,
                "CodeGeneration:RepositoryContextMaximumPromptCharacters must be positive.")
            .ValidateOnStart();

        services.AddSingleton<PlanningRequestContextProvider>();

        services.AddSingleton<PlanningDomainDiscoveryExecutor>(provider =>
            new PlanningDomainDiscoveryExecutor(
                provider.GetRequiredService<ILlmRouter>(),
                provider.GetRequiredService<IPromptBuilder<DomainDiscoveryPromptContext>>(),
                provider.GetRequiredService<ILlmStructuredOutputRepairer>(),
                provider.GetRequiredService<IOptions<FuwenPlanningOptions>>().Value.PlannerModel,
                provider.GetRequiredService<IOptions<FuwenPlanningOptions>>().Value.DomainMaxTokens));
        services.AddSingleton<PlanningTopologyExecutor>(provider =>
            new PlanningTopologyExecutor(
                provider.GetRequiredService<ILlmRouter>(),
                provider.GetRequiredService<IPromptBuilder<SolutionTopologyPromptContext>>(),
                provider.GetRequiredService<ILlmStructuredOutputRepairer>(),
                provider.GetRequiredService<IOptions<FuwenPlanningOptions>>().Value.PlannerModel,
                provider.GetRequiredService<IOptions<FuwenPlanningOptions>>().Value.TopologyMaxTokens));
        services.AddSingleton<PlanningContractExecutor>(provider =>
            new PlanningContractExecutor(
                provider.GetRequiredService<ILlmRouter>(),
                provider.GetRequiredService<IPromptBuilder<ContractDesignPromptContext>>(),
                provider.GetRequiredService<ILlmStructuredOutputRepairer>(),
                provider.GetRequiredService<IOptions<FuwenPlanningOptions>>().Value.PlannerModel,
                provider.GetRequiredService<IOptions<FuwenPlanningOptions>>().Value.ContractMaxTokens));
        services.AddSingleton<PlanningComponentExecutor>(provider =>
            new PlanningComponentExecutor(
                provider.GetRequiredService<ILlmRouter>(),
                provider.GetRequiredService<IPromptBuilder<ComponentDesignPromptContext>>(),
                provider.GetRequiredService<ILlmStructuredOutputRepairer>(),
                provider.GetRequiredService<IOptions<FuwenPlanningOptions>>().Value.PlannerModel,
                provider.GetRequiredService<IOptions<FuwenPlanningOptions>>().Value.ComponentMaxTokens));
        services.AddSingleton<BundleContractInputsActivity>();
        services.AddSingleton<FuwenStagedPlanningService>();
        services.AddSingleton<AssemblePlanningActivity>();
        services.AddDefaultPlanningArtifactCatalog();
        services.AddSingleton<StagedPlanningArtifactPublisher>();

        return services;
    }

    /// <summary>
    /// Registers a file-system-backed planning artifact catalog rooted at
    /// <paramref name="rootPath"/>. The catalog owns its content store and
    /// never touches the ambient <see cref="IArtifactRepository"/>, which in
    /// code-generation hosts publishes through workflow sessions. Planning
    /// runs then publish revisioned artifacts through
    /// <see cref="StagedPlanningArtifactPublisher"/>.
    /// </summary>
    public static IServiceCollection AddPlanningArtifactCatalog(
        this IServiceCollection services,
        string rootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        services.AddSingleton<IPlanningArtifactCatalog>(_ =>
            new PlanningArtifactCatalog(new FileSystemArtifactRepository(rootPath)));

        return services;
    }

    private static IServiceCollection AddDefaultPlanningArtifactCatalog(
        this IServiceCollection services)
    {
        services.TryAddSingleton<IPlanningArtifactCatalog>(provider =>
        {
            var root = provider
                .GetRequiredService<IOptions<FuwenPlanningOptions>>().Value.ArtifactRoot;
            if (string.IsNullOrWhiteSpace(root))
                root = "artifacts";
            if (!Path.IsPathRooted(root))
                root = Path.Combine(AppContext.BaseDirectory, root);
            return new PlanningArtifactCatalog(new FileSystemArtifactRepository(root));
        });

        return services;
    }
}
