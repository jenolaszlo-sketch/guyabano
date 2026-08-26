using Guyabano.Llm.Prompting;
using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning;

public sealed record ArchitectureDecisionIntegrationPromptContext(
    CodeGenerationPlan Plan,
    ArchitectureReview ResolvedReview,
    IReadOnlyList<ArchitectureGapResolution> ResolvedDecisions,
    string? PreviousFailure,
    string ResultToolName,
    IReadOnlyList<LlmTool> Tools,
    int MaxTokens = 8000,
    double Temperature = 0.1) : ILlmPromptContext;
