using System.Text.Json;
using Guyabano.CodeGeneration.Planning.Extensions;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Guyabano.Llm.Prompting.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Penghou.Baize.Claude;
using Penghou.Baize.Gemini;
using Penghou.Baize.Ollama;
using Penghou.Baize.OpenAi;
using Penghou.Baize.Router.Extensions;
using Penghou.Baize.Tools.Extensions;

// Thin live harness over FuwenStagedPlanningService (the web app consumes
// the same ICodeGenerationPlanningService via DI; this console exists so a
// human can smoke-test planning without the web stack).
// Usage: Guyabano.FuwenPlanning "<request>" [--workdir <dir>] [--context-file <path>] [--model <model>]
var request = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
string? FlagValue(string flag)
{
    var index = Array.FindIndex(
        args, arg => string.Equals(arg, flag, StringComparison.Ordinal));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var workdir = FlagValue("--workdir")
    ?? Path.Combine(Path.GetTempPath(), "guyabano-fuwen-live", Guid.NewGuid().ToString("N"));
var contextFile = FlagValue("--context-file");
var modelOverride = FlagValue("--model");
if (string.IsNullOrWhiteSpace(request))
{
    Console.Error.WriteLine("Usage: Guyabano.FuwenPlanning \"<request>\" [--workdir <dir>] [--context-file <path>] [--model <model>]");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient("llm", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: false,
    reloadOnChange: false);
builder.Services.AddOpenAiLlmProvider();
builder.Services.AddClaudeLlmProvider();
builder.Services.AddGeminiLlmProvider();
builder.Services.AddOllamaLlmProvider();
builder.Services.AddLlmRouting(builder.Configuration);
builder.Services.AddLlmTools();
builder.Services.AddLlmPrompting(Path.Combine(AppContext.BaseDirectory, "prompts"));
builder.Services.AddCodeGenerationPlanning();
builder.Services.AddFuwenPlanning(builder.Configuration);
using var host = builder.Build();
await host.StartAsync(cts.Token);
var services = host.Services;

var planning = services.GetRequiredService<FuwenStagedPlanningService>();
var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FuwenPlanningOptions>>().Value;
var model = string.IsNullOrWhiteSpace(modelOverride) ? options.PlannerModel : modelOverride;

Directory.CreateDirectory(workdir);

// The service contract takes an assembled request, mirroring
// CodeGenerationPlanningActivities.BuildPlanningRequest: repo context is
// a caller responsibility, not a service one.
var contextProvider = services.GetRequiredService<PlanningRequestContextProvider>();
var contextArguments = new List<Penghou.Fuwen.RuntimeArgument>
{
    new("request", Penghou.Fuwen.RuntimeValue.FromJson(
        JsonDocument.Parse(JsonSerializer.Serialize(request)).RootElement)),
    new("includeRepositoryContext", Penghou.Fuwen.RuntimeValue.FromJson(
        JsonDocument.Parse(options.IncludeRepositoryContextInPrompts || contextFile is not null ? "true" : "false").RootElement)),
    new("maxCharacters", Penghou.Fuwen.RuntimeValue.FromJson(
        JsonDocument.Parse(options.RepositoryContextMaximumPromptCharacters.ToString()).RootElement)),
};
if (contextFile is not null)
    contextArguments.Add(new("repositoryContext", Penghou.Fuwen.RuntimeValue.FromJson(
        JsonDocument.Parse(JsonSerializer.Serialize(await File.ReadAllTextAsync(contextFile, cts.Token))).RootElement)));
var assembled = await contextProvider.ExecuteAsync(
    new Penghou.Fuwen.ContextExecutionRequest(
        new Penghou.Fuwen.ExecutionInvocation(
            $"sha256:fuwen-execution/v3:{new string('a', 64)}",
            "runner/ctx", "run/runner/ctx", 1L,
            $"sha256:request/v1:{new string('b', 64)}"),
        PlanningFuwenDescriptors.Context(),
        contextArguments,
        new Penghou.Fuwen.PrimitiveType(Penghou.Fuwen.FuwenPrimitiveKind.String)),
    cts.Token);
var assembledRequest = ((Penghou.Fuwen.JsonRuntimeValue)assembled.Output!).Value.GetString()!;

var outcome = await planning.PlanAsync(assembledRequest, model, options.PlannerMaxTokens, null, cts.Token);
await File.WriteAllTextAsync(
    Path.Combine(workdir, "planning-outcome.json"),
    JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }),
    cts.Token);
if (!outcome.Succeeded || outcome.Plan is null)
{
    Console.Error.WriteLine($"Planning failed ({outcome.Failure}): {outcome.Error}");
    return 1;
}
Console.WriteLine(JsonSerializer.Serialize(outcome.Plan, new JsonSerializerOptions { WriteIndented = true }));
Console.Error.WriteLine($"Completed with {outcome.Plan.Tasks.Count} tasks in {workdir}.");
return 0;
