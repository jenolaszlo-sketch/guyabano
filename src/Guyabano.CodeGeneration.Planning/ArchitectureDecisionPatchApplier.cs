namespace Guyabano.CodeGeneration.Planning;

internal static class ArchitectureDecisionPatchApplier
{
    public static CodeGenerationPlan Apply(
        CodeGenerationPlan plan,
        ArchitectureReview resolvedReview,
        IReadOnlyList<ArchitectureGapResolution> resolvedDecisions,
        ArchitectureDecisionPatch patch)
    {
        ValidateResolutions(resolvedReview, patch);
        ValidateReplacementScope(resolvedReview, patch);
        ValidateDecisionRecords(plan, resolvedDecisions, patch);

        var result = new CodeGenerationPlan
        {
            Mission = plan.Mission,
            Title = plan.Title,
            Summary = plan.Summary,
            Assumptions = plan.Assumptions
                .Concat(patch.AssumptionsToAdd)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            Solution = plan.Solution,
            Projects = Replace(
                plan.Projects,
                patch.ProjectReplacements,
                item => item.Name),
            Modules = Replace(
                plan.Modules,
                patch.ModuleReplacements,
                item => item.Id),
            Contracts = ReplaceAndAdd(
                plan.Contracts,
                patch.ContractReplacements,
                patch.ContractAdditions,
                item => item.Id),
            Decisions = ReplaceAndAdd(
                plan.Decisions,
                patch.DecisionReplacements,
                patch.DecisionAdditions,
                item => item.Id),
            ArchitectureNotes = ReplaceAndAdd(
                plan.ArchitectureNotes,
                patch.ArchitectureNoteReplacements,
                patch.ArchitectureNoteAdditions,
                item => item.Id),
            UseCases = plan.UseCases,
            AcceptanceCriteria = ReplaceAndAdd(
                plan.AcceptanceCriteria,
                patch.AcceptanceCriterionReplacements,
                patch.AcceptanceCriterionAdditions,
                item => item.Id),
            Tasks = Replace(
                plan.Tasks,
                patch.TaskReplacements,
                item => item.Id)
        };

        var errors = CodeGenerationPlanValidator.Validate(result);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Architecture decision integration produced an invalid plan: {string.Join(" ", errors)}");
        return result;
    }

    private static void ValidateDecisionRecords(
        CodeGenerationPlan plan,
        IReadOnlyList<ArchitectureGapResolution> resolvedDecisions,
        ArchitectureDecisionPatch patch)
    {
        var existingIds = plan.Decisions.Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var replacements = patch.DecisionReplacements.ToLookup(
            item => item.Id,
            StringComparer.Ordinal);
        var additions = patch.DecisionAdditions.ToLookup(
            item => item.Id,
            StringComparer.Ordinal);

        foreach (var resolution in resolvedDecisions)
        {
            var expected = resolution.DecisionRecord;
            var candidates = existingIds.Contains(expected.Id)
                ? replacements[expected.Id]
                : additions[expected.Id];
            var actual = candidates.Count() == 1
                ? candidates.Single()
                : null;
            if (actual is null || !DecisionEquals(expected, actual))
                throw new InvalidOperationException(
                    $"Architecture decision patch must apply authoritative ADR '{expected.Id}' unchanged.");
        }
    }

    private static bool DecisionEquals(
        ArchitectureDecision expected,
        ArchitectureDecision actual) =>
        expected.Id.Equals(actual.Id, StringComparison.Ordinal) &&
        expected.Title.Equals(actual.Title, StringComparison.Ordinal) &&
        expected.Decision.Equals(actual.Decision, StringComparison.Ordinal) &&
        expected.Reasons.SequenceEqual(actual.Reasons, StringComparer.Ordinal) &&
        expected.AlternativesRejected.SequenceEqual(
            actual.AlternativesRejected,
            StringComparer.Ordinal) &&
        expected.RelatedPackages.SequenceEqual(
            actual.RelatedPackages,
            StringComparer.Ordinal);

    private static void ValidateResolutions(
        ArchitectureReview resolvedReview,
        ArchitectureDecisionPatch patch)
    {
        var expected = resolvedReview.Findings.Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var actual = patch.AppliedResolutionIds.ToArray();
        if (actual.Length != actual.Distinct(StringComparer.Ordinal).Count() ||
            !expected.SetEquals(actual))
            throw new InvalidOperationException(
                "Architecture decision patch must apply every resolved finding exactly once.");
    }

    private static void ValidateReplacementScope(
        ArchitectureReview resolvedReview,
        ArchitectureDecisionPatch patch)
    {
        var affectedIds = resolvedReview.Findings
            .SelectMany(item => item.AffectedIds)
            .ToHashSet(StringComparer.Ordinal);

        ValidateScoped(patch.ProjectReplacements, item => item.Name, affectedIds);
        ValidateScoped(patch.ModuleReplacements, item => item.Id, affectedIds);
        ValidateScoped(patch.ContractReplacements, item => item.Id, affectedIds);
        ValidateScoped(patch.DecisionReplacements, item => item.Id, affectedIds);
        ValidateScoped(
            patch.ArchitectureNoteReplacements,
            item => item.Id,
            affectedIds);
        ValidateScoped(
            patch.AcceptanceCriterionReplacements,
            item => item.Id,
            affectedIds);
        ValidateScoped(patch.TaskReplacements, item => item.Id, affectedIds);
    }

    private static void ValidateScoped<T>(
        IReadOnlyList<T> replacements,
        Func<T, string> id,
        IReadOnlySet<string> affectedIds)
    {
        var unrelated = replacements.Select(id)
            .Where(item => !affectedIds.Contains(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unrelated.Length > 0)
            throw new InvalidOperationException(
                $"Architecture decision patch replaces entities outside the resolved findings: {string.Join(", ", unrelated)}.");
    }

    private static List<T> Replace<T>(
        IReadOnlyList<T> source,
        IReadOnlyList<T> replacements,
        Func<T, string> id)
    {
        ValidateReplacementIds(source, replacements, id);
        var byId = replacements.ToDictionary(id, StringComparer.Ordinal);
        return source.Select(item =>
                byId.GetValueOrDefault(id(item), item))
            .ToList();
    }

    private static List<T> ReplaceAndAdd<T>(
        IReadOnlyList<T> source,
        IReadOnlyList<T> replacements,
        IReadOnlyList<T> additions,
        Func<T, string> id)
    {
        var result = Replace(source, replacements, id);
        var known = source.Select(id).ToHashSet(StringComparer.Ordinal);
        foreach (var addition in additions)
        {
            if (!known.Add(id(addition)))
                throw new InvalidOperationException(
                    $"Architecture addition '{id(addition)}' already exists.");
            result.Add(addition);
        }
        return result;
    }

    private static void ValidateReplacementIds<T>(
        IReadOnlyList<T> source,
        IReadOnlyList<T> replacements,
        Func<T, string> id)
    {
        var known = source.Select(id).ToHashSet(StringComparer.Ordinal);
        var replacementIds = replacements.Select(id).ToArray();
        if (replacementIds.Length !=
            replacementIds.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException(
                "Architecture decision patch contains duplicate replacements.");
        foreach (var replacementId in replacementIds.Where(item =>
                     !known.Contains(item)))
            throw new InvalidOperationException(
                $"Architecture replacement '{replacementId}' does not exist.");
    }
}
