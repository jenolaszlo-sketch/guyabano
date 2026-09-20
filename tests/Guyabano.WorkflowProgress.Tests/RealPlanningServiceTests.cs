#pragma warning disable xUnit1030
using System.Text.Json;
using Penghou.Guihua.Baize;
using Penghou.Guihua;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Guyabano.Llm.Prompting;
using Microsoft.Extensions.Options;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Nuwa;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Proves the web app can consume durable planning through the same
/// <see cref="ICodeGenerationPlanningService"/> interface: the Fuwen-backed
/// service runs domain, topology, per-context design, and assembly phases
/// against a scripted router and returns a usable outcome.
/// </summary>
public sealed class RealPlanningServiceTests
{
    private const string Model = "deepseek-v4-flash";

    [Fact]
    public async Task PlanAsync_returns_an_assembled_plan_through_fuwen_phases()
    {
        var ct = TestContext.Current.CancellationToken;
        var promptsRoot = FindPromptsRoot();
        var templateEngine = new ScribanPromptTemplateEngine(new FilePromptLoader(promptsRoot));
        var router = new ServiceStageRouter();
        var repairer = new LlmStructuredOutputRepairer(JsonRepairPipeline.Create());
        var options = Options.Create(new FuwenPlanningOptions());
        var service = new FuwenStagedPlanningService(
            router,
            new DomainDiscoveryPromptBuilder(templateEngine),
            new SolutionTopologyPromptBuilder(templateEngine),
            new ContractDesignPromptBuilder(templateEngine),
            new ComponentDesignPromptBuilder(templateEngine),
            new PlanningGapResolutionPromptBuilder(templateEngine),
            repairer,
            new FilePromptLoader(promptsRoot),
            options);

        var outcome = await service.PlanAsync("Plan todos and notes.", Model, 24000, null, ct);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.Failure.Should().Be(PlanningFailure.None);
        outcome.Model.Should().Be(Model);
        outcome.Plan.Should().NotBeNull();
        outcome.Plan!.Tasks.Should().HaveCountGreaterThanOrEqualTo(2);
        outcome.Plan.Tasks.Should().Contain(task => task.Id == "TASK-SCAFFOLD");
        outcome.StagedArtifacts.Should().NotBeNull();
        outcome.StagedArtifacts!.Topology.BoundedContexts.Should().HaveCount(2);
        outcome.StagedArtifacts.ContractCatalogs.Should().HaveCount(2);
        outcome.StagedArtifacts.ComponentManifests.Should().HaveCount(2);
        // 1 domain + 1 topology + 2 contracts + 2 manifests, no gap calls.
        router.RequestCalls.Should().Be(6);
    }

    private static string FindPromptsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "prompts", "domain-discovery", "system.sbn");
            if (File.Exists(candidate))
                return Path.Combine(directory.FullName, "prompts");
        }
        throw new DirectoryNotFoundException("Could not locate the Guyabano prompts root.");
    }

    private sealed class ServiceStageRouter : ILlmRouter
    {
        private const string DomainJson = """
            {
              "mission": {
                "guidingIntent": "Let users manage todos and notes.",
                "successOutcomes": ["Todos and notes can be added."],
                "constraints": [],
                "nonGoals": []
              },
              "title": "TodoNotes planner",
              "summary": "Plan a minimal todos and notes application.",
              "terms": [{ "name": "Todo", "definition": "A single trackable task." }],
              "capabilities": [
                { "name": "ManageTodos", "description": "CRUD for todos.", "businessRules": [] },
                { "name": "ManageNotes", "description": "CRUD for notes.", "businessRules": [] }
              ],
              "useCases": [
                {
                  "name": "AddTodo",
                  "capabilityName": "ManageTodos",
                  "actor": "User",
                  "objective": "Add a todo.",
                  "preconditions": [],
                  "inputs": [],
                  "businessRules": [],
                  "outcomes": ["Todo is stored."],
                  "errorOutcomes": [],
                  "acceptanceCriteria": [{
                    "scenario": "Add todo",
                    "given": [],
                    "when": [],
                    "then": [],
                    "verificationKinds": []
                  }]
                },
                {
                  "name": "AddNote",
                  "capabilityName": "ManageNotes",
                  "actor": "User",
                  "objective": "Add a note.",
                  "preconditions": [],
                  "inputs": [],
                  "businessRules": [],
                  "outcomes": ["Note is stored."],
                  "errorOutcomes": [],
                  "acceptanceCriteria": [{
                    "scenario": "Add note",
                    "given": [],
                    "when": [],
                    "then": [],
                    "verificationKinds": []
                  }]
                }
              ],
              "qualityAttributes": [],
              "assumptions": [],
              "inferredDefaults": [],
              "productAmbiguities": []
            }
            """;

        private const string TopologyJson = """
            {
              "solution": { "name": "TodoNotes", "path": "TodoNotes.slnx" },
              "projects": [{
                "name": "App",
                "path": "src/App/App.csproj",
                "kind": "Library",
                "role": "Application",
                "targetFramework": "net10.0",
                "responsibilities": ["Host tasks."],
                "projectDependencies": [],
                "packages": []
              }],
              "boundedContexts": [
                {
                  "name": "Todos",
                  "purpose": "Own todo management.",
                  "capabilityNames": ["ManageTodos"],
                  "dependsOnContextNames": [],
                  "inboundAdapters": [],
                  "outboundAdapters": []
                },
                {
                  "name": "Notes",
                  "purpose": "Own note management.",
                  "capabilityNames": ["ManageNotes"],
                  "dependsOnContextNames": [],
                  "inboundAdapters": [],
                  "outboundAdapters": []
                }
              ],
              "modules": [
                { "name": "M1", "boundedContextName": "Todos", "projectName": "App", "responsibilities": [] },
                { "name": "M2", "boundedContextName": "Notes", "projectName": "App", "responsibilities": [] }
              ],
              "decisions": []
            }
            """;

        private const string TodosCatalogJson = """
            {
              "boundedContextName": "Todos",
              "contracts": [{
                "name": "TodoContracts",
                "kind": "Contracts",
                "moduleName": "M1",
                "purpose": "Todo DTOs.",
                "members": [],
                "capabilityNames": ["ManageTodos"]
              }],
              "decisions": [],
              "inferredDefaults": []
            }
            """;

        private const string NotesCatalogJson = """
            {
              "boundedContextName": "Notes",
              "contracts": [{
                "name": "NoteContracts",
                "kind": "Contracts",
                "moduleName": "M2",
                "purpose": "Note DTOs.",
                "members": [],
                "capabilityNames": ["ManageNotes"]
              }],
              "decisions": [],
              "inferredDefaults": []
            }
            """;

        private const string TodosManifestJson = """
            {
              "boundedContextName": "Todos",
              "components": [{
                "name": "TodoService",
                "kind": "Service",
                "moduleName": "M1",
                "projectName": "App",
                "files": ["TodoService.cs"],
                "responsibilities": [],
                "definesContractNames": ["TodoContracts"],
                "implementsPortNames": [],
                "consumesContractNames": [],
                "usesConcreteComponentNames": [],
                "registersImplementationNames": [],
                "testsComponentNames": [],
                "capabilityNames": ["ManageTodos"],
                "acceptanceCriterionIds": ["AC-ADDTODO-ADD-TODO"],
                "lifetime": "Singleton",
                "complexityPoints": 2,
                "verificationKinds": ["Compilation"]
              }],
              "decisions": [],
              "inferredDefaults": []
            }
            """;

        private const string NotesManifestJson = """
            {
              "boundedContextName": "Notes",
              "components": [{
                "name": "NoteService",
                "kind": "Service",
                "moduleName": "M2",
                "projectName": "App",
                "files": ["NoteService.cs"],
                "responsibilities": [],
                "definesContractNames": ["NoteContracts"],
                "implementsPortNames": [],
                "consumesContractNames": [],
                "usesConcreteComponentNames": [],
                "registersImplementationNames": [],
                "testsComponentNames": [],
                "capabilityNames": ["ManageNotes"],
                "acceptanceCriterionIds": ["AC-ADDNOTE-ADD-NOTE"],
                "lifetime": "Singleton",
                "complexityPoints": 2,
                "verificationKinds": ["Compilation"]
              }],
              "decisions": [],
              "inferredDefaults": []
            }
            """;

        public int RequestCalls { get; private set; }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, LlmRequest request, CancellationToken cancellationToken)
        {
            RequestCalls++;
            var text = string.Concat(request.Messages
                .SelectMany(m => m.Parts)
                .OfType<LlmTextContent>()
                .Select(p => p.Text));
            string response;
            if (text.Contains("Create the solution topology", StringComparison.Ordinal))
                response = TopologyJson;
            else if (text.Contains("Design components for this bounded context:", StringComparison.Ordinal))
                response = FirstContextAfter(text, "Design components for this bounded context:") == "Notes"
                    ? NotesManifestJson : TodosManifestJson;
            else if (text.Contains("Design contracts for this bounded context:", StringComparison.Ordinal))
                response = FirstContextAfter(text, "Design contracts for this bounded context:") == "Notes"
                    ? NotesCatalogJson : TodosCatalogJson;
            else
                response = DomainJson;
            return StreamSingle(response);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ModelStrategy strategy, LlmRequest request, CancellationToken cancellationToken) =>
            StreamAsync(strategy.ToString(), request, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string model, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(model, builder.Build(ModelStrategy.Auto), cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ModelStrategy strategy, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(strategy.ToString(), builder, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
            string route, ILlmPromptBuilder builder, CancellationToken cancellationToken) =>
            StreamAsync(route, builder, cancellationToken);

        public IAsyncEnumerable<LlmStreamEvent> StreamRouteAsync(
            string route, LlmRequest request, CancellationToken cancellationToken) =>
            StreamAsync(route, request, cancellationToken);

        public ResolvedEndpoint Resolve(string model) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(string model, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ResolvedEndpoint Resolve(ModelStrategy strategy) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveAsync(ModelStrategy strategy, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ResolvedEndpoint> ResolveRouteAsync(string route, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainModelAsync(string model, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainStrategyAsync(ModelStrategy strategy, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LlmRouteExplanation> ExplainRouteAsync(string route, LlmRequest? request = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        private static string FirstContextAfter(string text, string marker)
        {
            var tail = text[(text.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            var match = System.Text.RegularExpressions.Regex.Match(
                tail, "\"name\"\\s*:\\s*\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : "unknown";
        }

        private static async IAsyncEnumerable<LlmStreamEvent> StreamSingle(
            string delta,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new LlmStreamEvent(delta, null, "stop", null, null, null, null, null, null);
        }
    }
}
