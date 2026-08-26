namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationWorkflowRequest(
    string Prompt,
    string? ResumeFromWorkflowId = null,
    CodeGenerationContinuationMode ContinuationMode =
        CodeGenerationContinuationMode.None,
    CodeGenerationWorkflowResult? ResumeFallback = null);
