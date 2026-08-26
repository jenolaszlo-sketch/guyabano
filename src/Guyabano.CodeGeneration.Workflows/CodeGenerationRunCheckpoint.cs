namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationRunCheckpoint(
    string WorkflowId,
    string Prompt,
    CodeGenerationWorkflowResult Result);
