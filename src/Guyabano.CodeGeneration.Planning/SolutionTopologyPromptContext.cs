using Penghou.Baize;
using Penghou.Guihua.Baize;
using Penghou.Guihua;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

public sealed record SolutionTopologyPromptContext(
    string OriginalRequest,
    DomainDiscovery Domain,
    LlmResponseFormat ResponseFormat,
    int MaxTokens,
    string? PreviousFailure = null) : ILlmPromptContext
{
    public double Temperature { get; init; } = 0.1;
}
