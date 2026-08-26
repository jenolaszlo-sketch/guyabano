namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationCheckpointLoadRequest(
    string SourceWorkflowId,
    string Prompt,
    CodeGenerationWorkflowResult? FallbackResult);
