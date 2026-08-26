using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning;

internal static class ArchitectureReviewSemanticRepair
{
    public static ArchitectureReview Repair(
        ArchitectureReview review,
        out IReadOnlyList<LlmRepairAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(review);

        var repairedFindings = new List<ArchitectureReviewFinding>(
            review.Findings.Count);
        var severityRepairs = 0;
        foreach (var finding in review.Findings)
        {
            var severity = finding.RequiresUserInput
                ? ArchitectureReviewSeverity.Blocking
                : finding.Severity;
            if (severity != finding.Severity)
                severityRepairs++;

            repairedFindings.Add(new ArchitectureReviewFinding
            {
                Id = finding.Id,
                Severity = severity,
                Category = finding.Category,
                Summary = finding.Summary,
                Evidence = [.. finding.Evidence],
                AffectedIds = [.. finding.AffectedIds],
                SuggestedResolution = finding.SuggestedResolution,
                RequiresUserInput = finding.RequiresUserInput
            });
        }

        var approved = !repairedFindings.Any(item =>
            item.Severity == ArchitectureReviewSeverity.Blocking);
        var approvalRepaired = approved != review.Approved;
        var repairAttempts = new List<LlmRepairAttempt>
        {
            new(
                "semantic/required-user-input-severity",
                severityRepairs > 0
                    ? LlmRepairStatus.Succeeded
                    : LlmRepairStatus.NotApplicable,
                Note: severityRepairs > 0
                    ? $"corrected {severityRepairs} finding(s)"
                    : null),
            new(
                "semantic/approval-consistency",
                approvalRepaired
                    ? LlmRepairStatus.Succeeded
                    : LlmRepairStatus.NotApplicable)
        };
        attempts = repairAttempts;

        return severityRepairs > 0 || approvalRepaired
            ? new ArchitectureReview
            {
                Approved = approved,
                Findings = repairedFindings
            }
            : review;
    }
}
