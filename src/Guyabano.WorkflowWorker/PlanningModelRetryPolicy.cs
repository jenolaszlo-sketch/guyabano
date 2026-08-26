using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowWorker;

internal enum PlanningModelFailureKind
{
    Output,
    Quality
}

internal static class PlanningModelRetryPolicy
{
    public static PlanningModelFailureKind Classify(PlanningFailure failure) =>
        failure == PlanningFailure.InvalidPlan
            ? PlanningModelFailureKind.Quality
            : PlanningModelFailureKind.Output;

    public static int MaximumAttempts(PlanningModelFailureKind kind) =>
        kind == PlanningModelFailureKind.Quality
            ? CodeGenerationWorkflowConstants
                .MaximumArchitectureModelQualityAttempts
            : CodeGenerationWorkflowConstants
                .MaximumArchitectureModelOutputAttempts;
}

internal sealed record PlanningModelRetryState(
    int OutputFailures = 0,
    int QualityFailures = 0,
    string? PreviousFailure = null)
{
    public int TotalFailures => OutputFailures + QualityFailures;

    public int Attempt(PlanningModelFailureKind kind) =>
        kind == PlanningModelFailureKind.Quality
            ? QualityFailures + 1
            : OutputFailures + 1;

    public PlanningModelRetryState Record(
        PlanningModelFailureKind kind,
        string? failure) =>
        kind == PlanningModelFailureKind.Quality
            ? this with
            {
                QualityFailures = QualityFailures + 1,
                PreviousFailure = failure
            }
            : this with
            {
                OutputFailures = OutputFailures + 1,
                PreviousFailure = failure
            };
}
