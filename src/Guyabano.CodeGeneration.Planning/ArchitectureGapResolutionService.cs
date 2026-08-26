using Microsoft.Extensions.Logging;
using Penghou.Baize;
using Penghou.Baize.Router;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

internal sealed class ArchitectureGapResolutionService(
    ILlmRouter router,
    IPromptBuilder<ArchitectureGapResolutionPromptContext> promptBuilder,
    ILlmStructuredOutputRepairer repairer,
    ILogger<ArchitectureGapResolutionService> logger)
    : IArchitectureGapResolutionService
{
    public async Task<ArchitectureGapResolutionOutcome> ResolveAsync(
        CodeGenerationPlan plan,
        ArchitectureReviewFinding finding,
        IReadOnlyList<ArchitecturePractice> practices,
        int architectureVersion,
        string model,
        int maxTokens,
        string? previousFailure = null,
        CancellationToken cancellationToken = default)
    {
        var decisionId = CreateDecisionId(
            architectureVersion + 1,
            finding.Id);
        var format = LlmResponseFormat.JsonSchema(
            JsonSchemaGenerator.GenerateSchemaJson<ArchitectureGapResolution>());
        var request = await promptBuilder.BuildAsync(
            new(
                plan,
                finding,
                practices,
                decisionId,
                format,
                maxTokens,
                previousFailure),
            cancellationToken);
        var response = await router.CompleteStreamingAsync(
            model,
            request,
            cancellationToken: cancellationToken);
        if (response is null)
            return Failed(PlanningFailure.NoResponse,
                "The architecture gap resolver returned no response.", model);

        var repaired = await repairer.RepairAsync(
            response,
            format,
            cancellationToken);
        var parsed = StructuredPlanningStageParser<ArchitectureGapResolution>
            .Parse(repaired);
        if (!parsed.Succeeded || parsed.Value is null)
            return Failed(
                MapFailure(parsed.Failure),
                parsed.Error ?? "The architecture resolution could not be parsed.",
                model,
                repaired);

        var resolution = NormalizePractice(practices, parsed.Value);
        var errors = Validate(
            plan,
            finding,
            practices,
            decisionId,
            resolution);
        if (errors.Count > 0)
            return Failed(
                PlanningFailure.InvalidPlan,
                string.Join(" ", errors),
                model,
                repaired);

        logger.LogInformation(
            "Resolved architecture finding {FindingId} with {Model}.",
            finding.Id,
            model);
        return new(true, PlanningFailure.None, null, model, resolution,
            repaired.ContentWasRepaired,
            repaired.ContentRepairAttempts ?? [])
        {
            Usage = repaired.Usage,
            Diagnostics = repaired.Diagnostics,
            FinishReason = repaired.FinishReason
        };
    }

    internal static IReadOnlyList<string> Validate(
        CodeGenerationPlan plan,
        ArchitectureReviewFinding finding,
        IReadOnlyList<ArchitecturePractice> practices,
        string decisionId,
        ArchitectureGapResolution resolution)
    {
        var errors = new List<string>();
        if (!resolution.FindingId.Equals(finding.Id, StringComparison.Ordinal))
            errors.Add($"Resolution must target finding '{finding.Id}'.");
        if (string.IsNullOrWhiteSpace(resolution.Decision) ||
            resolution.Reasons.Count == 0 ||
            resolution.Consequences.Count == 0)
            errors.Add("Resolution must contain a decision, reasons, and consequences.");
        if (!resolution.DecisionRecord.Id.Equals(
                decisionId,
                StringComparison.Ordinal))
            errors.Add($"Resolution ADR must use decision ID '{decisionId}'.");
        if (!resolution.DecisionRecord.Decision.Equals(
                resolution.Decision,
                StringComparison.Ordinal))
            errors.Add("Resolution decision and ADR decision must be identical.");
        errors.AddRange(
            ArchitectureDecisionPackageReferenceValidator.Validate(
                plan.Projects,
                resolution.DecisionRecord));
        ValidatePractice(practices, resolution, errors);
        var knownAffectedIds = finding.AffectedIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in resolution.AffectedIds.Where(id =>
                     !knownAffectedIds.Contains(id)))
            errors.Add($"Resolution references unexpected affected ID '{id}'.");
        if (resolution.RequiresUserInput &&
            string.IsNullOrWhiteSpace(resolution.UserQuestion))
            errors.Add("A user-required resolution must contain a user question.");
        return errors;
    }

    private static void ValidatePractice(
        IReadOnlyList<ArchitecturePractice> practices,
        ArchitectureGapResolution resolution,
        ICollection<string> errors)
    {
        var applied = resolution.AppliedPractice;
        if (string.IsNullOrWhiteSpace(applied.Id) ||
            string.IsNullOrWhiteSpace(applied.Title) ||
            string.IsNullOrWhiteSpace(applied.Guidance) ||
            string.IsNullOrWhiteSpace(applied.Applicability) ||
            applied.Reasons.Count == 0)
        {
            errors.Add("Resolution must identify a complete architecture practice.");
            return;
        }

        var existing = practices.SingleOrDefault(item =>
            item.Id.Equals(applied.Id, StringComparison.Ordinal));
        if (resolution.ReusedExistingPractice)
        {
            if (existing is null)
            {
                errors.Add(
                    $"Reused architecture practice '{applied.Id}' does not exist.");
                return;
            }

            return;
        }

        if (existing is not null)
            errors.Add(
                $"New architecture practice '{applied.Id}' already exists.");
        if (!applied.Scope.Equals("Project", StringComparison.Ordinal))
            errors.Add("A newly established practice must have Project scope.");
    }

    private static ArchitectureGapResolution NormalizePractice(
        IReadOnlyList<ArchitecturePractice> practices,
        ArchitectureGapResolution resolution)
    {
        if (!resolution.ReusedExistingPractice)
            return resolution;
        var canonical = practices.SingleOrDefault(item =>
            item.Id.Equals(
                resolution.AppliedPractice.Id,
                StringComparison.Ordinal));
        if (canonical is null)
            return resolution;
        return new ArchitectureGapResolution
        {
            FindingId = resolution.FindingId,
            ResolutionKind = resolution.ResolutionKind,
            Decision = resolution.Decision,
            DecisionRecord = resolution.DecisionRecord,
            AppliedPractice = canonical,
            ReusedExistingPractice = true,
            Reasons = resolution.Reasons,
            AlternativesConsidered = resolution.AlternativesConsidered,
            Consequences = resolution.Consequences,
            AffectedIds = resolution.AffectedIds,
            UserOverridable = resolution.UserOverridable,
            RequiresUserInput = resolution.RequiresUserInput,
            UserQuestion = resolution.UserQuestion
        };
    }

    private static string CreateDecisionId(
        int architectureVersion,
        string findingId)
    {
        var normalized = new string(findingId
            .Select(character => char.IsLetterOrDigit(character)
                ? char.ToUpperInvariant(character)
                : '-')
            .ToArray());
        return $"ADR-RESOLUTION-V{architectureVersion}-{normalized}";
    }

    private static PlanningFailure MapFailure(ToolCallParseFailure failure) =>
        failure switch
        {
            ToolCallParseFailure.TruncatedResponse =>
                PlanningFailure.TruncatedResponse,
            ToolCallParseFailure.SchemaValidationFailed =>
                PlanningFailure.SchemaValidationFailed,
            ToolCallParseFailure.DeserializationFailed =>
                PlanningFailure.DeserializationFailed,
            _ => PlanningFailure.InvalidToolArguments
        };

    private static ArchitectureGapResolutionOutcome Failed(
        PlanningFailure failure,
        string error,
        string model,
        LlmResponse? response = null) =>
        new(false, failure, error, model, null,
            response?.ContentWasRepaired ?? false,
            response?.ContentRepairAttempts ?? [])
        {
            Usage = response?.Usage,
            Diagnostics = response?.Diagnostics,
            FinishReason = response?.FinishReason
        };
}
