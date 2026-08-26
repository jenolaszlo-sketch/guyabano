using Penghou.Baize;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

public sealed record ContractDesignPromptContext(
    DomainDiscovery Domain,
    SolutionTopology Topology,
    BoundedContextPlan BoundedContext,
    IReadOnlyList<BoundedContextContractCatalog> UpstreamCatalogs,
    LlmResponseFormat ResponseFormat,
    int MaxTokens,
    string? PreviousFailure = null) : ILlmPromptContext
{
    public double Temperature { get; init; } = 0.1;
}
