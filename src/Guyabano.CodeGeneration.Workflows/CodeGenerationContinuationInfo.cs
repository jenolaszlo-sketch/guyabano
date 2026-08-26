namespace Guyabano.CodeGeneration.Workflows;

public sealed record CodeGenerationContinuationInfo(
    string SourceWorkflowId,
    CodeGenerationContinuationMode Mode);
