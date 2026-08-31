using Penghou.Baize;
using Guyabano.Llm.CodeGeneration;
using Guyabano.Messaging;

namespace Guyabano.WorkflowWorker;

internal static class CodeGenerationProgressDiagnostics
{
    public static IReadOnlyList<WorkflowDiagnostic> Create(
        CodeGenerationOutcome outcome)
    {
        var diagnostics = new List<WorkflowDiagnostic>();

        if (outcome.JsonWasRepaired)
        {
            var details = outcome.JsonRepairAttempts
                .Where(attempt =>
                    !IsUnchangedAttempt(attempt))
                .Select(attempt =>
                    $"{attempt.Name}: {attempt.Status}")
                .ToArray();

            diagnostics.Add(
                new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Warning,
                    "json-repaired",
                    "The model response required JSON repair.",
                    details));
        }

        if (outcome.Failure ==
            CodeGenerationFailure.SchemaValidationFailed)
        {
            var details = string.IsNullOrWhiteSpace(outcome.Error)
                ? []
                : new[] { outcome.Error };
            diagnostics.Add(
                new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    "schema-validation-failed",
                    "The generated tool arguments did not match the required schema.",
                    details.Concat(FailureFingerprint.Evidence(
                            outcome.Failure.ToString(),
                            outcome.Error))
                        .ToArray()));
        }

        if (outcome.Failure ==
            CodeGenerationFailure.MissingToolCall)
        {
            var details = new List<string>();

            if (!string.IsNullOrWhiteSpace(outcome.Error))
            {
                details.Add(outcome.Error);
            }

            if (!string.IsNullOrWhiteSpace(outcome.FinishReason))
            {
                details.Add($"Finish reason: {outcome.FinishReason}");
            }

            if (outcome.Usage?.CompletionTokens is { } completionTokens)
            {
                details.Add($"Completion tokens: {completionTokens}");
            }

            var exhaustedOutput = outcome.FinishReason?.Equals(
                "length",
                StringComparison.OrdinalIgnoreCase) == true;

            diagnostics.Add(
                new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    exhaustedOutput
                        ? "output-limit-exhausted"
                        : "missing-tool-call",
                    exhaustedOutput
                        ? "The model exhausted its output budget before returning the required tool call."
                        : "The model did not return the required tool call.",
                    details));
        }

        return diagnostics;
    }

    private static bool IsUnchangedAttempt(LlmRepairAttempt attempt) =>
        attempt.Status is
            LlmRepairStatus.Skipped or
            LlmRepairStatus.NotApplicable;
}
