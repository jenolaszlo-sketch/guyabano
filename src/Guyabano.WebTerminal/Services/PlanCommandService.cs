using System.Text.Json;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Microsoft.Extensions.Options;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Guihua;
using Penghou.Guihua.Baize;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Guyabano.WorkflowWorker;

namespace Guyabano.WebTerminal.Services;

/// <summary>Authored plan plus its admission verdict for UI display.</summary>
public sealed record PlanCommandResult(
    string Dsl,
    bool Admitted,
    IReadOnlyList<string> Diagnostics,
    int Attempts,
    string Model);

/// <summary>Executed plan output for UI display.</summary>
public sealed record PlanExecutionResult(
    string? Output,
    string? Error,
    string? Input = null);

/// <summary>
/// Handles <c>/plan</c> chat commands: authors a Fuwen workflow from the
/// request through <see cref="WorkflowAuthor"/> and returns the DSL plus
/// its admission verdict for display. Never executes anything.
/// </summary>
public sealed class PlanCommandService(
    WorkflowAuthor author,
    PlanCommandCatalogue catalogueSource,
    FuwenZhinuExecutionPorts executionPorts,
    IOptions<CodeGenerationWorkerOptions> options) : IPlanCommandService
{
    public const string CommandPrefix = "/plan";
    public const string RunPrefix = "/run";

    private WorkflowAdmissionResult? lastAdmission;
    private string? lastRequest;

    public static bool IsPlanCommand(string prompt) =>
        prompt.TrimStart().StartsWith(CommandPrefix, StringComparison.OrdinalIgnoreCase);

    public static string StripPrefix(string prompt)
    {
        var trimmed = prompt.TrimStart();
        var rest = trimmed.StartsWith(CommandPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[CommandPrefix.Length..]
            : trimmed;
        return rest.TrimStart();
    }

    public async Task<PlanCommandResult?> AuthorPlanAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (!IsPlanCommand(prompt))
            return null;
        var request = StripPrefix(prompt);
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(request))
            return new PlanCommandResult(
                string.Empty, false, ["Usage: /plan <request>"], 0, settings.PlannerModel);
        var result = await author.AuthorAsync(
            request,
            catalogueSource.Summary,
            settings.PlannerModel,
            settings.PlannerMaxTokens,
            cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && result.Admission is not null)
        {
            lastAdmission = result.Admission;
            lastRequest = request;
        }
        else
        {
            lastAdmission = null;
            lastRequest = null;
        }
        return new PlanCommandResult(
            result.Dsl,
            result.Succeeded,
            result.Succeeded
                ? []
                : result.Diagnostics.Count > 0
                    ? result.Diagnostics
                    : ["The model did not produce an admittable workflow within the attempt budget."],
            result.Attempts.Count,
            settings.PlannerModel);
    }

    public static bool IsRunCommand(string prompt) =>
        prompt.TrimStart().StartsWith(RunPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Executes the last admitted author-plan with the original request as
    /// input on an isolated durable engine. Never executes unadmitted text.
    /// </summary>
    public async Task<PlanExecutionResult> ExecutePlanAsync(CancellationToken cancellationToken = default)
    {
        var admission = lastAdmission;
        var request = lastRequest;
        if (admission?.Receipt is null || admission.Compilation.Plan is null || request is null)
            return new PlanExecutionResult(null, "No admitted plan. Author one first with /plan <request>.");
        var plan = admission.Compilation.Plan;
        var settings = options.Value;
        var root = Path.Combine(
            string.IsNullOrWhiteSpace(settings.OutputRoot) ? "generated" : settings.OutputRoot,
            ".gen", "fuwen-runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            var registration = await new FuwenZhinuWorkflowFactory(
                    new InMemoryWorkflowDefinitionStore(),
                    new FuwenZhinuProviderRuntimeIdentity(
                        admission.Receipt.CatalogueSnapshotRevision,
                        admission.Receipt.ResolvedDescriptorSetFingerprint),
                    executionPorts)
                .CreateAsync(plan.Name, plan.Revision, admission, cancellationToken)
                .ConfigureAwait(false);
            await using var engine = new WorkflowEngine(
                store,
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(100) });
            using var input = JsonDocument.Parse(JsonSerializer.Serialize(request));
            var runId = await engine.StartAsync(
                plan.Name, plan.Revision, input.RootElement.Clone(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await engine.ExecuteAsync(runId, cancellationToken).ConfigureAwait(false);
            var output = await engine.WaitForCompletionAsync<JsonElement>(
                runId, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new PlanExecutionResult(
                JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }),
                null,
                request);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PlanExecutionResult(null, exception.Message);
        }
    }

}
