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
