using Guyabano.CI.Server;
using Guyabano.CI.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<CiServerOptions>()
    .Bind(builder.Configuration.GetSection(CiServerOptions.SectionName))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.GeneratedRoot),
        "CI:GeneratedRoot is required.")
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.DotNetCommand),
        "CI:DotNetCommand is required.")
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.JetBrainsCommand),
        "CI:JetBrainsCommand is required.")
    .ValidateOnStart();

builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<CiServerOptions>>()
        .Value;

    return new SafePathResolver(options.GeneratedRoot);
});
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<ProjectTargetResolver>();
builder.Services.AddSingleton<DotNetDiagnosticParser>();
builder.Services.AddScoped<DotNetScaffoldingStreamingService>();
builder.Services.AddScoped<DotNetBuildStreamingService>();
builder.Services.AddScoped<DotNetTestStreamingService>();
builder.Services.AddScoped<JetBrainsAnalysisStreamingService>();
builder.Services.AddControllers();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapControllers();

app.Run();
