using Guyabano.WebTerminal.Components;
using Guyabano.WebTerminal.Services;
using Guyabano.WorkflowWorker;
using Guyabano.WorkflowWorker.Extensions;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);
CodeGenerationConfiguration.AddDefaults(
    builder.Configuration,
    Path.Combine(
        AppContext.BaseDirectory,
        "appsettings.CodeGeneration.json"),
    args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddGuyabanoCodeGeneration(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<
    IAuthenticatedActorProvider,
    HttpContextApprovalActorProvider>();
builder.Services.AddScoped<
    ICodeGenerationWorkflowClient,
    CodeGenerationWorkflowClient>();
builder.Services.AddSingleton<PlanCommandCatalogue>(provider =>
{
    var configuration = provider.GetRequiredService<IConfiguration>();
    var section = configuration.GetSection(
        CodeGenerationWorkerOptions.SectionName);
    return PlanCommandCatalogueFactory.Create(
        Path.Combine(AppContext.BaseDirectory, "prompts"),
        section["PlannerModel"] ?? "deepseek-v4-flash",
        int.TryParse(section["PlannerMaxTokens"], out var maxTokens) ? maxTokens : 24000);
});
Guyabano.CodeGeneration.Planning.Extensions.ServiceCollectionExtensions
    .AddFuwenPlanning(builder.Services, builder.Configuration);
builder.Services.AddSingleton<Penghou.Fuwen.Zhinu.FuwenZhinuExecutionPorts>(provider =>
{
    var model = provider.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<Guyabano.CodeGeneration.Planning.Fuwen.FuwenPlanningOptions>>().Value;
    return new Penghou.Fuwen.Zhinu.FuwenZhinuExecutionPorts(
        new RejectingPlanActivity(),
        provider.GetRequiredService<
            Guyabano.CodeGeneration.Planning.Fuwen.PlanningRequestContextProvider>(),
        new Guyabano.CodeGeneration.Planning.Fuwen.PlanningDomainDiscoveryExecutor(
            provider.GetRequiredService<Penghou.Baize.Router.ILlmRouter>(),
            provider.GetRequiredService<Guyabano.Llm.Prompting.IPromptBuilder<Guyabano.CodeGeneration.Planning.DomainDiscoveryPromptContext>>(),
            provider.GetRequiredService<Penghou.Baize.Tools.ILlmStructuredOutputRepairer>(),
            model.PlannerModel,
            model.DomainMaxTokens));
});
builder.Services.AddScoped<IPlanCommandService, PlanCommandService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

sealed class RejectingPlanActivity : Penghou.Fuwen.IActivityExecutor
{
    public ValueTask<Penghou.Fuwen.ActivityExecutionResult> ExecuteAsync(
        Penghou.Fuwen.ActivityExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            $"No activity executor is bound for '{request.Activity.Name}'.");
}
