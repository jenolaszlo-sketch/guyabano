using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
using Penghou.Zhinu.Hosting;
using Penghou.Zhinu.Sqlite;
using Guyabano.Artifacts;
using Guyabano.CI.Client.Extensions;
using Guyabano.CodeGeneration.Planning.Extensions;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.CodeGeneration.Extensions;
using Guyabano.Llm.Prompting.Extensions;
using Guyabano.Messaging.Extensions;

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

        services.AddCangjieSqlite(options =>
        {
            options.DatabasePath = Path.Combine(stateRoot, "cangjie.db");
        });
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
        services.AddZhinuStep<
            PlanCodeGenerationStep,
            CodeGenerationWorkflowRequest,
            CodeGenerationWorkflowResult>(
                CodeGenerationWorkflowConstants.PlanStep);
        services.AddZhinuStep<
            DecomposeCodeGenerationTaskStep,
            CodeGenerationDecompositionWorkflowRequest,
            CodeGenerationDecompositionWorkflowResult>(
                CodeGenerationWorkflowConstants.DecomposeTaskStep);
        services.AddZhinuStep<
            ReviewCodeGenerationArchitectureStep,
            ArchitectureReviewWorkflowRequest,
            ArchitectureReviewWorkflowResult>(
                CodeGenerationWorkflowConstants.ReviewArchitectureStep);
        services.AddZhinuStep<
            ResolveCodeGenerationArchitectureGapStep,
            ArchitectureGapResolutionWorkflowRequest,
            ArchitectureGapResolutionWorkflowResult>(
                CodeGenerationWorkflowConstants.ResolveArchitectureGapStep);
        services.AddZhinuStep<
            IntegrateCodeGenerationArchitectureStep,
            ArchitectureDecisionIntegrationWorkflowRequest,
            ArchitectureDecisionIntegrationWorkflowResult>(
                CodeGenerationWorkflowConstants.IntegrateArchitectureStep);
        services.AddZhinuStep<
            ScaffoldCodeGenerationStep,
            CodeGenerationScaffoldingRequest,
            CodeGenerationScaffoldingResult>(
                CodeGenerationWorkflowConstants.ScaffoldStep);
        services.AddZhinuStep<
            GenerateCodeTaskStep,
            CodeGenerationTaskWorkflowRequest,
            CodeGenerationTaskWorkflowResult>(
                CodeGenerationWorkflowConstants.GenerateTaskStep);
        services.AddZhinuStep<
            BuildGeneratedCodeStep,
            CodeGenerationBuildRequest,
            CodeGenerationBuildResult>(
                CodeGenerationWorkflowConstants.BuildStep);
        services.AddZhinuStep<
            LoadCodeGenerationCheckpointStep,
            CodeGenerationCheckpointLoadRequest,
            CodeGenerationRunCheckpoint>(
                CodeGenerationWorkflowConstants.LoadCheckpointStep);
        services.AddZhinuStep<
            SaveCodeGenerationCheckpointStep,
            CodeGenerationCheckpointRequest,
            Guyabano.Artifacts.ArtifactReference>(
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
        services.AddSingleton<IArtifactRepository>(provider =>
            new ContextIndexingArtifactRepository(
                provider.GetRequiredService<FileSystemArtifactRepository>(),
                provider.GetRequiredService<IContextStore>()));

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
        services.AddGuyabanoCiClient(configuration);
        services.AddLlmTools();
        services.AddLlmPrompting(
            Path.Combine(AppContext.BaseDirectory, "prompts"));
        services.AddLlmCodeGeneration();
        services.AddCodeGenerationPlanning();
        services.AddWorkflowProgress();

        services.AddScoped<CodeGenerationPlanningActivities>();
        services.AddScoped<CodeGenerationDecompositionActivities>();
        services.AddScoped<CodeGenerationArchitectureActivities>();
        services.AddScoped<CodeGenerationScaffoldingActivities>();
        services.AddScoped<CodeGenerationTaskActivities>();
        services.AddScoped<CodeGenerationBuildActivities>();
        services.AddScoped<CodeGenerationCheckpointActivities>();
        services.AddHostedService<ModelConfigurationLoggingService>();

        return services;
    }
}
