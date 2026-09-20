using Penghou.Baize;
using Penghou.Guihua.Baize;

namespace Guyabano.Llm.Prompting;

public sealed record CodeGenerationTaskPromptContext(
    CodeGenerationTaskContext Task,
    string ResultToolName,
    IReadOnlyList<LlmTool> Tools,
    int MaxTokens = 8000,
    double Temperature = 0.1) : ILlmPromptContext;
