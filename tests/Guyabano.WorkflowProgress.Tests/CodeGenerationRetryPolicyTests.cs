using FluentAssertions;
using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.CodeGeneration;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationRetryPolicyTests
{
    [Theory]
    [InlineData(CodeGenerationFailure.NoResponse)]
    [InlineData(CodeGenerationFailure.MissingToolCall)]
    [InlineData(CodeGenerationFailure.InvalidToolArguments)]
    [InlineData(CodeGenerationFailure.SchemaValidationFailed)]
    [InlineData(CodeGenerationFailure.DeserializationFailed)]
    [InlineData(CodeGenerationFailure.EmptyResult)]
    [InlineData(CodeGenerationFailure.IncompleteProject)]
    [InlineData(CodeGenerationFailure.OutOfScopeFiles)]
    [InlineData(CodeGenerationFailure.NoChanges)]
    [InlineData(CodeGenerationFailure.FileValidationFailed)]
    public void ShouldRetry_RecoverableModelFailure_OnFirstAttempt(
        CodeGenerationFailure failure)
    {
        var outcome = CreateOutcome(failure);

        CodeGenerationRetryPolicy.ShouldRetry(outcome, attempt: 1)
            .Should()
            .BeTrue();
        CodeGenerationWorkflowConstants.MaximumGenerateAttempts
            .Should()
            .Be(
                CodeGenerationWorkflowConstants.MaximumModelTiers *
                CodeGenerationWorkflowConstants.MaximumAttemptsPerModel);
    }

    [Theory]
    [InlineData(CodeGenerationFailure.NoResponse)]
    [InlineData(CodeGenerationFailure.MissingToolCall)]
    [InlineData(CodeGenerationFailure.InvalidToolArguments)]
    [InlineData(CodeGenerationFailure.SchemaValidationFailed)]
    [InlineData(CodeGenerationFailure.DeserializationFailed)]
    [InlineData(CodeGenerationFailure.EmptyResult)]
    [InlineData(CodeGenerationFailure.IncompleteProject)]
    [InlineData(CodeGenerationFailure.OutOfScopeFiles)]
    [InlineData(CodeGenerationFailure.NoChanges)]
    [InlineData(CodeGenerationFailure.FileValidationFailed)]
    public void ShouldRetry_RecoverableModelFailure_StopsAtMaximumAttempts(
        CodeGenerationFailure failure)
    {
        var outcome = CreateOutcome(failure);

        CodeGenerationRetryPolicy.ShouldRetry(
                outcome,
                attempt: CodeGenerationWorkflowConstants
                    .MaximumGenerateAttempts)
            .Should()
            .BeFalse();
    }

    [Theory]
    [InlineData(CodeGenerationFailure.None)]
    [InlineData(CodeGenerationFailure.EmissionFailed)]
    public void ShouldRetry_NonRecoverableFailure_ReturnsFalse(
        CodeGenerationFailure failure) =>
        CodeGenerationRetryPolicy.ShouldRetry(
                CreateOutcome(failure),
                attempt: 1)
            .Should()
            .BeFalse();

    private static CodeGenerationOutcome CreateOutcome(
        CodeGenerationFailure failure) =>
        new(
            Succeeded: false,
            Failure: failure,
            Error: "Generation failed.",
            Model: "test-model",
            JsonWasRepaired: false,
            JsonRepairAttempts: [],
            WrittenFiles: [],
            SkippedFiles: []);
}
