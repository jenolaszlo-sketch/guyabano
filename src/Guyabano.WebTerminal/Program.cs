using Guyabano.WebTerminal.Components;
using Guyabano.WebTerminal.Services;
using Guyabano.WorkflowWorker;
using Guyabano.WorkflowWorker.Extensions;

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
