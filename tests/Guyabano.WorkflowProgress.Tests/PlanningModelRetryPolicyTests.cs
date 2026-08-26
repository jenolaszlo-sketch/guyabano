using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class PlanningModelRetryPolicyTests
{
    [Theory]
    [InlineData(PlanningFailure.NoResponse)]
    [InlineData(PlanningFailure.MissingToolCall)]
    [InlineData(PlanningFailure.InvalidToolArguments)]
    [InlineData(PlanningFailure.SchemaValidationFailed)]
    [InlineData(PlanningFailure.DeserializationFailed)]
    public void Classify_StructuralFailure_UsesOutputBudget(
        PlanningFailure failure)
    {
        var kind = PlanningModelRetryPolicy.Classify(failure);

        kind.Should().Be(PlanningModelFailureKind.Output);
        PlanningModelRetryPolicy.MaximumAttempts(kind).Should().Be(
            CodeGenerationWorkflowConstants
                .MaximumArchitectureModelOutputAttempts);
    }

    [Fact]
    public void Classify_InvalidPlan_UsesIndependentQualityBudget()
    {
        var kind = PlanningModelRetryPolicy.Classify(
            PlanningFailure.InvalidPlan);

        kind.Should().Be(PlanningModelFailureKind.Quality);
        PlanningModelRetryPolicy.MaximumAttempts(kind).Should().Be(
            CodeGenerationWorkflowConstants
                .MaximumArchitectureModelQualityAttempts);
    }

    [Fact]
    public void State_TracksOutputAndQualityFailuresIndependently()
    {
        var state = new PlanningModelRetryState()
            .Record(PlanningModelFailureKind.Output, "bad schema")
            .Record(PlanningModelFailureKind.Quality, "unknown module");

        state.OutputFailures.Should().Be(1);
        state.QualityFailures.Should().Be(1);
        state.TotalFailures.Should().Be(2);
        state.Attempt(PlanningModelFailureKind.Output).Should().Be(2);
        state.Attempt(PlanningModelFailureKind.Quality).Should().Be(2);
        state.PreviousFailure.Should().Be("unknown module");
    }
}
