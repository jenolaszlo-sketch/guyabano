namespace Guyabano.CodeGeneration.Planning;

internal static class ArchitectureReviewValidator
{
    public static IReadOnlyList<string> Validate(
        CodeGenerationPlan plan,
        ArchitectureReview review)
    {
        var errors = new List<string>();
        var ids = plan.Projects.Select(item => item.Name)
            .Concat(plan.Modules.Select(item => item.Id))
            .Concat(plan.Contracts.Select(item => item.Id))
            .Concat(plan.Decisions.Select(item => item.Id))
            .Concat(plan.ArchitectureNotes.Select(item => item.Id))
            .Concat(plan.UseCases.Select(item => item.Id))
            .Concat(plan.AcceptanceCriteria.Select(item => item.Id))
            .Concat(plan.Tasks.Select(item => item.Id))
            .Append(plan.Solution.Name)
            .Append("MISSION")
            .ToHashSet(StringComparer.Ordinal);
        var findingIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var finding in review.Findings)
        {
            if (string.IsNullOrWhiteSpace(finding.Id) ||
                !findingIds.Add(finding.Id))
                errors.Add($"Duplicate or empty review finding ID '{finding.Id}'.");
            if (string.IsNullOrWhiteSpace(finding.Category) ||
                string.IsNullOrWhiteSpace(finding.Summary) ||
                string.IsNullOrWhiteSpace(finding.SuggestedResolution))
                errors.Add($"Review finding '{finding.Id}' is incomplete.");
            if (finding.Evidence.Count == 0)
                errors.Add($"Review finding '{finding.Id}' contains no evidence.");
            if (finding.RequiresUserInput &&
                finding.Severity != ArchitectureReviewSeverity.Blocking)
                errors.Add($"Review finding '{finding.Id}' requires user input but is not blocking.");
            if (finding.RequiresUserInput &&
                !finding.Category.Equals(
                    "ProductAmbiguity",
                    StringComparison.Ordinal))
                errors.Add($"Review finding '{finding.Id}' requires user input but is not categorized as ProductAmbiguity.");
            foreach (var id in finding.AffectedIds.Where(id =>
                         !ids.Contains(id)))
                errors.Add($"Review finding '{finding.Id}' references unknown architecture ID '{id}'.");
        }

        var hasBlocking = review.Findings.Any(item =>
            item.Severity == ArchitectureReviewSeverity.Blocking);
        if (review.Approved == hasBlocking)
            errors.Add("Architecture approval must be false exactly when blocking findings exist.");
        return errors;
    }
}
