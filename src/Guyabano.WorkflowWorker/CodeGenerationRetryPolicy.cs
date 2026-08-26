using Guyabano.CodeGeneration.Workflows;
using Guyabano.Llm.CodeGeneration;

namespace Guyabano.WorkflowWorker;

internal static class CodeGenerationRetryPolicy
{
    public static bool ShouldRetry(
        CodeGenerationOutcome outcome,
        int attempt,
        int maximumAttempts =
            CodeGenerationWorkflowConstants.MaximumGenerateAttempts) =>
        IsRetryable(outcome.Failure) &&
        attempt < maximumAttempts;

    private static bool IsRetryable(
        CodeGenerationFailure failure) => failure is
        CodeGenerationFailure.NoResponse or
        CodeGenerationFailure.MissingToolCall or
        CodeGenerationFailure.InvalidToolArguments or
        CodeGenerationFailure.SchemaValidationFailed or
        CodeGenerationFailure.DeserializationFailed or
        CodeGenerationFailure.EmptyResult or
        CodeGenerationFailure.IncompleteProject or
        CodeGenerationFailure.OutOfScopeFiles or
        CodeGenerationFailure.NoChanges or
        CodeGenerationFailure.FileValidationFailed;
}
