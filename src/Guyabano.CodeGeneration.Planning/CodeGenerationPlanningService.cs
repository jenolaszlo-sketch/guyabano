using Microsoft.Extensions.Logging;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

internal sealed class CodeGenerationPlanningService(
    ILlmRouter llmRouter,
    IPromptBuilder<DomainDiscoveryPromptContext> domainPromptBuilder,
    IPromptBuilder<SolutionTopologyPromptContext> topologyPromptBuilder,
    IPromptBuilder<ContractDesignPromptContext> contractPromptBuilder,
    IPromptBuilder<ComponentDesignPromptContext> componentPromptBuilder,
    IPromptBuilder<PlanningGapResolutionPromptContext> gapPromptBuilder,
    ILlmStructuredOutputRepairer structuredOutputRepairer,
    ILogger<CodeGenerationPlanningService> logger)
    : ICodeGenerationPlanningService
{
    private const int MaximumStageAttempts = 3;

    public async Task<CodeGenerationPlanningOutcome> PlanAsync(
        string request,
        string model,
        int maxTokens = 12000,
        string? previousFailure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var trace = new PlanningTrace();
        var domain = await ExecuteStageAsync<DomainDiscovery>(
            "domain discovery",
            request,
            model,
            Math.Min(maxTokens, 8000),
            null,
            (format, tokens, failure) => domainPromptBuilder.BuildAsync(
                new DomainDiscoveryPromptContext(
                    request,
                    format,
                    tokens,
                    failure),
                cancellationToken),
            StagedPlanningValidator.ValidateDomain,
            trace,
            cancellationToken);
        if (!domain.Succeeded || domain.Value is null)
            return Failed(domain, model, trace);

        var domainValue = domain.Value;
        if (domainValue.ProductAmbiguities.Count > 0)
        {
            var resolvedDomain = await ResolveDomainAmbiguitiesAsync(
                request,
                domainValue,
                model,
                Math.Min(maxTokens, 6000),
                trace,
                cancellationToken);
            if (!resolvedDomain.Succeeded || resolvedDomain.Value is null)
                return Failed(resolvedDomain, model, trace);
            domainValue = resolvedDomain.Value;
        }

        var architectureFailure = previousFailure;
        for (var architecturePass = 1; architecturePass <= 2;
             architecturePass++)
        {
            var topology = await ExecuteStageAsync<SolutionTopology>(
                "solution topology",
                request,
                model,
                Math.Min(maxTokens, 10000),
                architectureFailure,
                (format, tokens, failure) => topologyPromptBuilder.BuildAsync(
                    new SolutionTopologyPromptContext(
                        request,
                        domainValue,
                        format,
                        tokens,
                        failure),
                    cancellationToken),
                value => StagedPlanningValidator.ValidateTopology(
                    domainValue,
                    value),
                trace,
                cancellationToken);
            if (!topology.Succeeded || topology.Value is null)
                return Failed(topology, model, trace);

            IReadOnlyList<BoundedContextPlan> orderedContexts;
            try
            {
                orderedContexts = OrderContexts(topology.Value.BoundedContexts);
            }
            catch (InvalidOperationException exception)
            {
                return Failed(
                    PlanningFailure.InvalidPlan,
                    exception.Message,
                    model,
                    trace);
            }

            var catalogs = new List<BoundedContextContractCatalog>();
            foreach (var context in orderedContexts)
            {
                var upstreamNames = GetTransitiveDependencies(
                    context,
                    topology.Value.BoundedContexts);
                var upstream = catalogs.Where(item =>
                        upstreamNames.Contains(item.BoundedContextName))
                    .ToArray();
                var catalog = await ExecuteStageAsync<BoundedContextContractCatalog>(
                    $"contract design for {context.Name}",
                    request,
                    model,
                    Math.Min(maxTokens, 12000),
                    architectureFailure,
                    (format, tokens, failure) => contractPromptBuilder.BuildAsync(
                        new ContractDesignPromptContext(
                            domainValue,
                            topology.Value,
                            context,
                            upstream,
                            format,
                            tokens,
                            failure),
                        cancellationToken),
                    value => ValidateCatalog(
                        context,
                        value,
                        domainValue,
                        topology.Value,
                        catalogs),
                    trace,
                    cancellationToken);
                if (!catalog.Succeeded || catalog.Value is null)
                    return Failed(catalog, model, trace);
                catalogs.Add(catalog.Value);
            }

            var manifests = new List<BoundedContextComponentManifest>();
            foreach (var context in orderedContexts)
            {
                var upstreamNames = GetTransitiveDependencies(
                    context,
                    topology.Value.BoundedContexts);
                var upstream = manifests.Where(item =>
                        upstreamNames.Contains(item.BoundedContextName))
                    .ToArray();
                var manifest = await ExecuteStageAsync<BoundedContextComponentManifest>(
                    $"component design for {context.Name}",
                    request,
                    model,
                    Math.Min(maxTokens, 16000),
                    architectureFailure,
                    (format, tokens, failure) => componentPromptBuilder.BuildAsync(
                        new ComponentDesignPromptContext(
                            domainValue,
                            topology.Value,
                            context,
                            catalogs,
                            upstream,
                            format,
                            tokens,
                            failure),
                        cancellationToken),
                    value => ValidateManifest(
                        context,
                        value,
                        domainValue,
                        topology.Value,
                        catalogs,
                        manifests),
                    trace,
                    cancellationToken);
                if (!manifest.Succeeded || manifest.Value is null)
                    return Failed(manifest, model, trace);
                manifests.Add(manifest.Value);
            }

            var artifacts = new StagedPlanningArtifacts(
                domainValue,
                topology.Value,
                catalogs,
                manifests);
            CodeGenerationPlan plan;
            try
            {
                plan = StagedCodeGenerationPlanAssembler.Assemble(artifacts);
            }
            catch (InvalidOperationException exception)
            {
                if (architecturePass < 2)
                {
                    architectureFailure =
                        "Cross-stage architecture reconciliation is required. " +
                        exception.Message;
                    logger.LogWarning(
                        "Architecture pass {ArchitecturePass} produced an invalid assembled plan. Routing the failure to topology, contract, and component stages: {Error}",
                        architecturePass,
                        exception.Message);
                    continue;
                }
                return Failed(
                    PlanningFailure.InvalidPlan,
                    exception.Message,
                    model,
                    trace);
            }

            logger.LogInformation(
                "Staged planning completed with {ContextCount} contexts, {ContractCount} contracts, {ComponentCount} components, and {TaskCount} executable tasks using {Model}.",
                topology.Value.BoundedContexts.Count,
                catalogs.Sum(item => item.Contracts.Count),
                manifests.Sum(item => item.Components.Count),
                plan.Tasks.Count,
                model);

            return new CodeGenerationPlanningOutcome(
                true,
                PlanningFailure.None,
                null,
                model,
                plan,
                trace.WasRepaired,
                trace.RepairAttempts)
            {
                Usage = trace.Usage,
                Diagnostics = trace.LastResponse?.Diagnostics,
                FinishReason = trace.LastResponse?.FinishReason,
                StagedArtifacts = artifacts
            };
        }

        throw new InvalidOperationException(
            "The bounded architecture reconciliation loop ended without a result.");
    }

    private async Task<StageResult<T>> ExecuteStageAsync<T>(
        string stage,
        string originalRequest,
        string model,
        int maxTokens,
        string? initialFailure,
        Func<LlmResponseFormat, int, string?, Task<LlmRequest>> buildRequest,
        Func<T, IReadOnlyList<string>> validate,
        PlanningTrace trace,
        CancellationToken cancellationToken,
        bool allowSemanticResolution = true)
    {
        var responseFormat = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<T>());
        var previousFailure = initialFailure;
        StageResult<T>? lastFailure = null;
        T? lastArtifact = default;

        for (var attempt = 1; attempt <= MaximumStageAttempts; attempt++)
        {
            logger.LogInformation(
                "Planning stage {Stage}, attempt {Attempt} of {MaximumAttempts}, started using {Model}.",
                stage,
                attempt,
                MaximumStageAttempts,
                model);
            var request = await buildRequest(
                responseFormat,
                maxTokens,
                previousFailure);
            var response = await llmRouter.CompleteStreamingAsync(
                model,
                request,
                cancellationToken: cancellationToken);
            if (response is null)
            {
                lastFailure = StageResult<T>.Failed(
                    PlanningFailure.NoResponse,
                    $"The {stage} stage returned no response.");
            }
            else
            {
                var repaired = await structuredOutputRepairer.RepairAsync(
                    response,
                    responseFormat,
                    cancellationToken);
                trace.Add(repaired);
                var parsed = StructuredPlanningStageParser<T>.Parse(repaired);
                if (!parsed.Succeeded || parsed.Value is null)
                {
                    lastFailure = StageResult<T>.Failed(
                        MapFailure(parsed.Failure),
                        $"The {stage} stage returned invalid structured output: {parsed.Error ?? "Parsing failed."}");
                }
                else
                {
                    lastArtifact = parsed.Value;
                    var errors = validate(parsed.Value);
                    if (errors.Count == 0)
                    {
                        logger.LogInformation(
                            "Planning stage {Stage} completed on attempt {Attempt}.",
                            stage,
                            attempt);
                        return StageResult<T>.Success(parsed.Value);
                    }

                    lastFailure = StageResult<T>.Failed(
                        PlanningFailure.InvalidPlan,
                        $"The {stage} stage failed validation: {string.Join(" ", errors)}");
                }
            }

            previousFailure = lastFailure.Error;
            logger.LogWarning(
                "Planning stage {Stage}, attempt {Attempt}, failed: {Error}",
                stage,
                attempt,
                lastFailure.Error);
        }

        if (allowSemanticResolution &&
            lastFailure?.Failure == PlanningFailure.InvalidPlan &&
            lastArtifact is not null)
        {
            var resolution = await ResolveGapAsync(
                originalRequest,
                stage,
                PlanningPromptJson.Serialize(lastArtifact),
                lastFailure.Error!,
                model,
                Math.Min(maxTokens, 6000),
                trace,
                cancellationToken);
            if (!resolution.Succeeded || resolution.Value is null)
                return StageResult<T>.Failed(
                    resolution.Failure,
                    resolution.Error!);
            if (resolution.Value.RequiresUserInput)
                return StageResult<T>.Failed(
                    PlanningFailure.InvalidPlan,
                    resolution.Value.UserQuestion);

            var guidance = BuildResolutionGuidance(
                lastFailure.Error!,
                resolution.Value);
            return await ExecuteStageAsync(
                stage,
                originalRequest,
                model,
                maxTokens,
                guidance,
                buildRequest,
                validate,
                trace,
                cancellationToken,
                allowSemanticResolution: false);
        }

        return lastFailure!;
    }

    private async Task<StageResult<DomainDiscovery>>
        ResolveDomainAmbiguitiesAsync(
            string originalRequest,
            DomainDiscovery domain,
            string model,
            int maxTokens,
            PlanningTrace trace,
            CancellationToken cancellationToken)
    {
        var artifactJson = PlanningPromptJson.Serialize(domain);
        var tasks = domain.ProductAmbiguities.Select(ambiguity =>
            ResolveGapAsync(
                originalRequest,
                "domain ambiguity resolution",
                artifactJson,
                ambiguity.Question,
                model,
                maxTokens,
                trace,
                cancellationToken)).ToArray();
        var resolutions = await Task.WhenAll(tasks);
        var failed = resolutions.FirstOrDefault(item =>
            !item.Succeeded || item.Value is null);
        if (failed is not null)
            return StageResult<DomainDiscovery>.Failed(
                failed.Failure,
                failed.Error!);

        var resolvedValues = resolutions.Select(item => item.Value!).ToArray();
        var requiresUserInput = resolvedValues.FirstOrDefault(item =>
            item.RequiresUserInput);
        if (requiresUserInput is not null)
            return StageResult<DomainDiscovery>.Failed(
                PlanningFailure.InvalidPlan,
                requiresUserInput.UserQuestion);

        logger.LogInformation(
            "Resolved {AmbiguityCount} domain ambiguity or scope question(s) before topology design.",
            domain.ProductAmbiguities.Count);
        return StageResult<DomainDiscovery>.Success(
            ApplyDomainResolutions(domain, resolvedValues));
    }

    internal static DomainDiscovery ApplyDomainResolutions(
        DomainDiscovery domain,
        IReadOnlyList<PlanningGapResolution> resolutions)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(resolutions);
        if (domain.ProductAmbiguities.Count != resolutions.Count)
            throw new ArgumentException(
                "Every domain ambiguity must have exactly one resolution.",
                nameof(resolutions));
        var defaults = domain.ProductAmbiguities
            .Zip(resolutions)
            .Select(pair => new DiscoveredDomainDefault
            {
                Kind = pair.Second.ResolutionKind,
                Subject = pair.First.Question,
                MissingInformation = pair.First.WhyItMatters,
                Decision = pair.Second.Decision,
                Reasons = [.. pair.Second.Reasons],
                Impact = string.Join(" ", pair.Second.Consequences),
                AffectedCapabilities = [.. pair.First.AffectedCapabilities],
                UserOverridable = pair.Second.UserOverridable
            });
        return new DomainDiscovery
        {
            Mission = domain.Mission,
            Title = domain.Title,
            Summary = domain.Summary,
            Terms = domain.Terms,
            Capabilities = domain.Capabilities,
            UseCases = domain.UseCases,
            QualityAttributes = domain.QualityAttributes,
            Assumptions = domain.Assumptions,
            InferredDefaults = domain.InferredDefaults.Concat(defaults).ToList(),
            ProductAmbiguities = []
        };
    }

    private async Task<StageResult<PlanningGapResolution>> ResolveGapAsync(
        string originalRequest,
        string stage,
        string artifactJson,
        string issue,
        string model,
        int maxTokens,
        PlanningTrace trace,
        CancellationToken cancellationToken)
    {
        var format = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<PlanningGapResolution>());
        string? previousFailure = null;
        StageResult<PlanningGapResolution>? lastFailure = null;
        for (var attempt = 1; attempt <= MaximumStageAttempts; attempt++)
        {
            var request = await gapPromptBuilder.BuildAsync(
                new PlanningGapResolutionPromptContext(
                    originalRequest,
                    stage,
                    artifactJson,
                    issue,
                    format,
                    maxTokens,
                    previousFailure),
                cancellationToken);
            var response = await llmRouter.CompleteStreamingAsync(
                model,
                request,
                cancellationToken: cancellationToken);
            if (response is null)
            {
                lastFailure = StageResult<PlanningGapResolution>.Failed(
                    PlanningFailure.NoResponse,
                    $"The focused resolver for {stage} returned no response.");
            }
            else
            {
                var repaired = await structuredOutputRepairer.RepairAsync(
                    response,
                    format,
                    cancellationToken);
                trace.Add(repaired);
                var parsed = StructuredPlanningStageParser<PlanningGapResolution>
                    .Parse(repaired);
                if (parsed.Succeeded && parsed.Value is not null &&
                    !string.IsNullOrWhiteSpace(parsed.Value.Decision) &&
                    parsed.Value.Reasons.Count > 0 &&
                    parsed.Value.Consequences.Count > 0 &&
                    (!parsed.Value.RequiresUserInput ||
                     !string.IsNullOrWhiteSpace(parsed.Value.UserQuestion)))
                    return StageResult<PlanningGapResolution>.Success(
                        parsed.Value);
                lastFailure = StageResult<PlanningGapResolution>.Failed(
                    parsed.Succeeded
                        ? PlanningFailure.InvalidPlan
                        : MapFailure(parsed.Failure),
                    parsed.Error ??
                    "A focused resolution must contain a decision, reasons, consequences, and a question when user input is required.");
            }
            previousFailure = lastFailure.Error;
        }
        return lastFailure!;
    }

    private static string BuildResolutionGuidance(
        string validationError,
        PlanningGapResolution resolution) =>
        $"The stage artifact was rejected: {validationError} " +
        $"A focused resolver selected this correction: {resolution.Decision} " +
        $"Reasons: {string.Join("; ", resolution.Reasons)}. " +
        $"Consequences: {string.Join("; ", resolution.Consequences)}. " +
        "Apply this correction exactly, preserve all valid artifact content, and return a complete corrected stage artifact.";

    private static IReadOnlyList<BoundedContextPlan> OrderContexts(
        IReadOnlyList<BoundedContextPlan> contexts)
    {
        var byName = contexts.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var remaining = contexts.ToDictionary(
            item => item.Name,
            item => item.DependsOnContextNames.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var result = new List<BoundedContextPlan>();
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(item => item.Value.Count == 0)
                .Select(item => item.Key)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
                throw new InvalidOperationException(
                    "Bounded-context dependencies contain a cycle.");
            foreach (var name in ready)
            {
                result.Add(byName[name]);
                remaining.Remove(name);
                foreach (var dependencies in remaining.Values)
                    dependencies.Remove(name);
            }
        }
        return result;
    }

    private static HashSet<string> GetTransitiveDependencies(
        BoundedContextPlan context,
        IReadOnlyList<BoundedContextPlan> contexts)
    {
        var byName = contexts.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(context.DependsOnContextNames);
        while (pending.TryPop(out var name))
        {
            if (!result.Add(name))
                continue;
            foreach (var dependency in byName[name].DependsOnContextNames)
                pending.Push(dependency);
        }
        return result;
    }

    private static IReadOnlyList<string> ValidateCatalog(
        BoundedContextPlan expectedContext,
        BoundedContextContractCatalog catalog,
        DomainDiscovery domain,
        SolutionTopology topology,
        IReadOnlyList<BoundedContextContractCatalog> existing)
    {
        var errors = StagedPlanningValidator.ValidateContracts(
                domain,
                topology,
                [.. existing, catalog])
            .ToList();
        if (!catalog.BoundedContextName.Equals(
                expectedContext.Name,
                StringComparison.Ordinal))
            errors.Add(
                $"Contract catalog must target bounded context '{expectedContext.Name}', not '{catalog.BoundedContextName}'.");
        return errors;
    }

    private static IReadOnlyList<string> ValidateManifest(
        BoundedContextPlan expectedContext,
        BoundedContextComponentManifest manifest,
        DomainDiscovery domain,
        SolutionTopology topology,
        IReadOnlyList<BoundedContextContractCatalog> catalogs,
        IReadOnlyList<BoundedContextComponentManifest> existing)
    {
        var errors = StagedPlanningValidator.ValidateComponents(
                domain,
                topology,
                catalogs,
                [.. existing, manifest])
            .ToList();
        if (!manifest.BoundedContextName.Equals(
                expectedContext.Name,
                StringComparison.Ordinal))
            errors.Add(
                $"Component manifest must target bounded context '{expectedContext.Name}', not '{manifest.BoundedContextName}'.");
        var implementedCapabilities = manifest.Components
            .SelectMany(item => item.CapabilityNames)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var capabilityName in expectedContext.CapabilityNames.Where(
                     item => !implementedCapabilities.Contains(item)))
            errors.Add(
                $"Component manifest for '{expectedContext.Name}' does not implement capability '{capabilityName}'.");
        return errors;
    }

    private static PlanningFailure MapFailure(ToolCallParseFailure failure) =>
        failure switch
        {
            ToolCallParseFailure.MissingToolCall => PlanningFailure.MissingToolCall,
            ToolCallParseFailure.EmptyArguments or ToolCallParseFailure.InvalidJson =>
                PlanningFailure.InvalidToolArguments,
            ToolCallParseFailure.TruncatedResponse =>
                PlanningFailure.TruncatedResponse,
            ToolCallParseFailure.SchemaValidationFailed =>
                PlanningFailure.SchemaValidationFailed,
            ToolCallParseFailure.DeserializationFailed =>
                PlanningFailure.DeserializationFailed,
            _ => PlanningFailure.InvalidToolArguments
        };

    private static CodeGenerationPlanningOutcome Failed<T>(
        StageResult<T> stage,
        string model,
        PlanningTrace trace) => Failed(
            stage.Failure,
            stage.Error!,
            model,
            trace);

    private static CodeGenerationPlanningOutcome Failed(
        PlanningFailure failure,
        string error,
        string model,
        PlanningTrace trace) =>
        new(false, failure, error, model, null, trace.WasRepaired, trace.RepairAttempts)
        {
            Usage = trace.Usage,
            Diagnostics = trace.LastResponse?.Diagnostics,
            FinishReason = trace.LastResponse?.FinishReason
        };

    private sealed record StageResult<T>(
        bool Succeeded,
        T? Value,
        PlanningFailure Failure,
        string? Error)
    {
        public static StageResult<T> Success(T value) =>
            new(true, value, PlanningFailure.None, null);

        public static StageResult<T> Failed(
            PlanningFailure failure,
            string error) => new(false, default, failure, error);
    }

    private sealed class PlanningTrace
    {
        private readonly object gate = new();
        private readonly List<LlmRepairAttempt> repairAttempts = [];
        private int? promptTokens;
        private int? completionTokens;
        private int? totalTokens;
        private int? promptCacheHitTokens;
        private int? promptCacheMissTokens;

        public bool WasRepaired { get; private set; }
        public IReadOnlyList<LlmRepairAttempt> RepairAttempts => repairAttempts;
        public LlmResponse? LastResponse { get; private set; }
        public LlmUsage? Usage =>
            promptTokens is null && completionTokens is null && totalTokens is null
                ? null
                : new LlmUsage(
                    promptTokens,
                    completionTokens,
                    totalTokens,
                    promptCacheHitTokens,
                    promptCacheMissTokens);

        public void Add(LlmResponse response)
        {
            lock (gate)
            {
                LastResponse = response;
                WasRepaired |= response.ContentWasRepaired;
                if (response.ContentRepairAttempts is not null)
                    repairAttempts.AddRange(response.ContentRepairAttempts);
                promptTokens = Sum(promptTokens, response.Usage?.PromptTokens);
                completionTokens = Sum(
                    completionTokens,
                    response.Usage?.CompletionTokens);
                totalTokens = Sum(totalTokens, response.Usage?.TotalTokens);
                promptCacheHitTokens = Sum(
                    promptCacheHitTokens,
                    response.Usage?.PromptCacheHitTokens);
                promptCacheMissTokens = Sum(
                    promptCacheMissTokens,
                    response.Usage?.PromptCacheMissTokens);
            }
        }

        private static int? Sum(int? left, int? right) =>
            left is null && right is null ? null : (left ?? 0) + (right ?? 0);
    }
}
