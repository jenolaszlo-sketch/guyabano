namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationCheckpointRequest(
    string WorkflowId,
    string Prompt,
    CodeGenerationWorkflowResult Result);
