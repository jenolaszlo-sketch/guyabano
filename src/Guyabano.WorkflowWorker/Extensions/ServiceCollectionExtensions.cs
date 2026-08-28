using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Penghou.Baize.Claude;
using Penghou.Baize.Diagnostics;
using Penghou.Baize.Gemini;
using Penghou.Baize.Ollama;
using Penghou.Baize.OpenAi;
using Penghou.Baize.Router.Extensions;
using Penghou.Baize.Tools.Extensions;
using Penghou.Cangjie;
using Penghou.Cangjie.Sqlite;
using Penghou.Hetu;
using Penghou.Hetu.CSharp;
using Penghou.Hetu.Ladybug;
using Penghou.Zhinu.Hosting;
using Penghou.Zhinu.Sqlite;
using Guyabano.Artifacts;
using Guyabano.CI.Client.Extensions;
using Guyabano.CodeGeneration.Planning.Extensions;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.CodeGeneration.Extensions;
using Guyabano.Llm.Prompting.Extensions;
using Guyabano.Messaging.Extensions;
using Guyabano.Messaging;
using Guyabano.Session;

namespace Guyabano.WorkflowWorker.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGuyabanoCodeGeneration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<CodeGenerationWorkerOptions>()
            .Bind(configuration.GetSection(
                CodeGenerationWorkerOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Model),
                "CodeGeneration:Model is required.")
            .Validate(
                CodeGenerationModelSelector.HasValidConfiguration,
                $"CodeGeneration:EscalationModels may contain up to {CodeGenerationWorkflowConstants.MaximumModelTiers - 1} distinct fallback model(s), all different from CodeGeneration:Model.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PlannerModel),
                "CodeGeneration:PlannerModel is required.")
            .Validate(
                options => options.PlannerMaxTokens > 0,
                "CodeGeneration:PlannerMaxTokens must be positive.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.DecompositionModel),
                "CodeGeneration:DecompositionModel is required.")
            .Validate(
                DecompositionModelSelector.HasValidConfiguration,
                "CodeGeneration:DecompositionEscalationModels is optional, but any configured fallback must be distinct from CodeGeneration:DecompositionModel.")
            .Validate(
                options => options.DecompositionMaxTokens > 0 &&
                    options.DecompositionRetryMaxTokens >=
                    options.DecompositionMaxTokens,
                "Code-generation decomposition token limits must be positive and the retry limit cannot be lower than the initial limit.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ArchitectureReviewModel),
                "CodeGeneration:ArchitectureReviewModel is required.")
            .Validate(
                options => options.ArchitectureReviewMaxTokens > 0,
                "CodeGeneration:ArchitectureReviewMaxTokens must be positive.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ArchitectureIntegratorModel),
                "CodeGeneration:ArchitectureIntegratorModel is required.")
            .Validate(
                options => options.ArchitectureIntegratorMaxTokens > 0,
                "CodeGeneration:ArchitectureIntegratorMaxTokens must be positive.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ProjectName),
                "CodeGeneration:ProjectName is required.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.OutputRoot),
                "CodeGeneration:OutputRoot is required.")
            .Validate(
                options => !options.RepositoryContextEnabled ||
                    !string.IsNullOrWhiteSpace(options.RepositoryId),
                "CodeGeneration:RepositoryId is required when repository context is enabled.")
            .Validate(
                options => options.RepositoryContextMaximumPromptCharacters > 0,
                "CodeGeneration:RepositoryContextMaximumPromptCharacters must be positive.")
            .Validate(
                options => options.DefaultMaxTokens > 0 &&
                    options.DefaultRetryMaxTokens > 0 &&
                    options.ModelTokenBudgets.Values.All(budget =>
                        budget.MaxTokens > 0 &&
                        budget.RetryMaxTokens > 0),
                "All code-generation token budgets must be positive.")
            .ValidateOnStart();

        services.AddBaizeHttpDiagnostics(options =>
        {
            options.Enabled = true;
            options.DirectoryPath = Path.Combine("logs", "http");
        });

        var outputRoot = configuration[
                $"{CodeGenerationWorkerOptions.SectionName}:OutputRoot"]
            ?? "generated";
        var stateRoot = Path.Combine(outputRoot, ".gen");
        Directory.CreateDirectory(stateRoot);

        services.AddSingleton<IGuyabanoSessionStore>(
            new FileSystemGuyabanoSessionStore(
                Path.Combine(stateRoot, "sessions")));
        services.AddSingleton<ISessionEventStore>(
            new FileSystemSessionEventStore(
                Path.Combine(stateRoot, "sessions", "events")));

        services.AddCangjieSqlite(options =>
        {
            options.DatabasePath = Path.Combine(stateRoot, "cangjie.db");
        });
        services.AddSingleton<CangjieRevisionedConceptService>();
        services.TryAddSingleton(_ => new HetuHostBuilder()
            .AddCSharpPlugin()
            .UseLadybugStore(Path.Combine(stateRoot, "hetu"))
            .Build());
        services.TryAddSingleton<
            IRepositoryContextService,
            RepositoryContextService>();
        services.AddZhinuSqlite(options =>
        {
            options.DatabasePath = Path.Combine(stateRoot, "zhinu.db");
        });
        services.AddZhinu(options =>
        {
            options.MaxConcurrentWorkflows = 2;
            options.ScanBatchSize = 10;
        });
        services.AddZhinuWorkflow<
            CodeGenerationWorkflow,
            CodeGenerationWorkflowRequest,
            CodeGenerationWorkflowResult>(
            CodeGenerationWorkflowConstants.WorkflowName,
            CodeGenerationWorkflowConstants.WorkflowVersion);
        services.AddSingleton<CodeGenerationActivityHeartbeatStore>();
        services.AddSingleton<CodeGenerationWorkspaceResolver>();
        services.AddSingleton<CodeGenerationRepositoryReindexer>();
        services.AddZhinuStep<ReindexGeneratedWorkspaceStep>(
                CodeGenerationWorkflowConstants.ReindexStep);
        services.AddZhinuStep<IndexRepositoryStep>(
                CodeGenerationWorkflowConstants.IndexRepositoryStep);
        services.AddZhinuStep<SelectRepositoryContextStep>(
                CodeGenerationWorkflowConstants.SelectRepositoryContextStep);
        services.AddZhinuStep<CaptureRepositoryContextStep>(
                CodeGenerationWorkflowConstants.CaptureRepositoryContextStep);
        services.AddZhinuStep<PlanCodeGenerationStep>(
                CodeGenerationWorkflowConstants.PlanStep);
        services.AddZhinuStep<DecomposeCodeGenerationTaskStep>(
                CodeGenerationWorkflowConstants.DecomposeTaskStep);
        services.AddZhinuStep<ReviewCodeGenerationArchitectureStep>(
                CodeGenerationWorkflowConstants.ReviewArchitectureStep);
        services.AddZhinuStep<ResolveCodeGenerationArchitectureGapStep>(
                CodeGenerationWorkflowConstants.ResolveArchitectureGapStep);
        services.AddZhinuStep<IntegrateCodeGenerationArchitectureStep>(
                CodeGenerationWorkflowConstants.IntegrateArchitectureStep);
        services.AddZhinuStep<ScaffoldCodeGenerationStep>(
                CodeGenerationWorkflowConstants.ScaffoldStep);
        services.AddZhinuStep<GenerateCodeTaskStep>(
                CodeGenerationWorkflowConstants.GenerateTaskStep);
        services.AddZhinuStep<BuildGeneratedCodeStep>(
                CodeGenerationWorkflowConstants.BuildStep);
        services.AddZhinuStep<LoadCodeGenerationCheckpointStep>(
                CodeGenerationWorkflowConstants.LoadCheckpointStep);
        services.AddZhinuStep<SaveCodeGenerationCheckpointStep>(
                CodeGenerationWorkflowConstants.SaveCheckpointStep);

        services.AddSingleton<FileSystemArtifactRepository>(provider =>
        {
            var codeGeneration = provider
                .GetRequiredService<
                    Microsoft.Extensions.Options.IOptions<
                        CodeGenerationWorkerOptions>>()
                .Value;
            return new FileSystemArtifactRepository(
                Path.Combine(codeGeneration.OutputRoot, ".gen"));
        });
        services.AddSingleton<ContextIndexingArtifactRepository>(provider =>
            new ContextIndexingArtifactRepository(
                provider.GetRequiredService<FileSystemArtifactRepository>(),
                provider.GetRequiredService<IContextStore>()));
        services.AddSingleton<IArtifactRepository>(provider =>
            new ZhinuPublishingArtifactRepository(
                provider.GetRequiredService<
                    ContextIndexingArtifactRepository>(),
                provider.GetRequiredService<
                    CodeGenerationWorkspaceResolver>()));

        services.AddHttpClient("llm", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddLogging(logging => logging.AddFilter(
            "System.Net.Http.HttpClient.llm",
            LogLevel.None));

        services.AddOpenAiLlmProvider();
        services.AddClaudeLlmProvider();
        services.AddGeminiLlmProvider();
        services.AddOllamaLlmProvider();
        services.AddLlmRouting(configuration);
        WrapLlmRouterWithProvenance(services);
        services.AddGuyabanoCiClient(configuration);
        services.AddLlmTools();
        services.AddLlmPrompting(
            Path.Combine(AppContext.BaseDirectory, "prompts"));
        services.AddLlmCodeGeneration();
        services.AddCodeGenerationPlanning();
        services.AddWorkflowProgress();
        services.AddSingleton<IWorkflowProgressPublisher>(provider =>
            new ZhinuWorkflowProgressPublisher(
                provider.GetRequiredService<InMemoryWorkflowProgressHub>()));

        services.AddScoped<CodeGenerationPlanningActivities>();
        services.AddScoped<CodeGenerationDecompositionActivities>();
        services.AddScoped<CodeGenerationArchitectureActivities>();
        services.AddScoped<CodeGenerationScaffoldingActivities>();
        services.AddScoped<CodeGenerationTaskActivities>();
        services.AddScoped<CodeGenerationBuildActivities>();
        services.AddScoped<CodeGenerationCheckpointActivities>();
        services.AddSingleton<CodeGenerationWorkflowRestartService>();
        services.AddSingleton<CodeGenerationImpactAnalysisService>();
        services.AddSingleton<CodeGenerationStagingService>();
        services.AddSingleton<SessionClarificationService>();
        services.AddSingleton<SessionConsistencyAuditService>();
        services.AddHostedService<ModelConfigurationLoggingService>();

        return services;
    }

    private sealed class BaizeProvenanceRouterInner
    {
        public BaizeProvenanceRouterInner(Penghou.Baize.Router.ILlmRouter inner) => Inner = inner;
        public Penghou.Baize.Router.ILlmRouter Inner { get; }
    }

    /// <summary>
    /// Wraps the Baize <see cref="Penghou.Baize.Router.ILlmRouter"/> registration so every
    /// model invocation persists bounded execution provenance, without disturbing the
    /// router factory or its configuration.
    /// </summary>
    private static void WrapLlmRouterWithProvenance(IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(
            service => service.ServiceType == typeof(Penghou.Baize.Router.ILlmRouter));
        if (descriptor is null)
            return;

        services.Remove(descriptor);
        services.AddSingleton(provider => new BaizeProvenanceRouterInner(
            ResolveRouterDescriptor(provider, descriptor)));
        services.AddSingleton<Penghou.Baize.Router.ILlmRouter>(provider =>
            new BaizeExecutionProvenanceRouter(
                provider.GetRequiredService<BaizeProvenanceRouterInner>().Inner,
                provider.GetRequiredService<IArtifactRepository>(),
                provider.GetRequiredService<ILogger<BaizeExecutionProvenanceRouter>>()));
    }

    private static Penghou.Baize.Router.ILlmRouter ResolveRouterDescriptor(
        IServiceProvider provider,
        Microsoft.Extensions.DependencyInjection.ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationFactory is not null)
            return (Penghou.Baize.Router.ILlmRouter)descriptor.ImplementationFactory(provider);
        if (descriptor.ImplementationInstance is not null)
            return (Penghou.Baize.Router.ILlmRouter)descriptor.ImplementationInstance;
        if (descriptor.ImplementationType is not null)
            return (Penghou.Baize.Router.ILlmRouter)Microsoft.Extensions.DependencyInjection.ActivatorUtilities
                .CreateInstance(provider, descriptor.ImplementationType);
        throw new InvalidOperationException(
            "The Baize router registration cannot be resolved for provenance wrapping.");
    }
}
