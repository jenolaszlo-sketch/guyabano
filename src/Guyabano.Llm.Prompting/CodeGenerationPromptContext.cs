using Penghou.Baize;

namespace Guyabano.Llm.Prompting;

public sealed record CodeGenerationPromptContext(
    string Task,
    string ResultToolName,
    string ProjectName,
    string RootNamespace,
    string TargetFramework,
    IReadOnlyList<LlmTool>? Tools = null,
    string? ProjectContext = null,
    IReadOnlyList<ProjectFileContext>? Files = null,
    int MaxTokens = 8000,
    double Temperature = 0.2) : ILlmPromptContext;
