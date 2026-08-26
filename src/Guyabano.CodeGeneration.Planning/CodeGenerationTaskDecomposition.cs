using Penghou.Baize.Tools.Schema;
using System.Text.Json.Serialization;

namespace Guyabano.CodeGeneration.Planning;

public sealed class CodeGenerationTaskDecomposition
{
    [JsonPropertyName("parentTaskId")]
    public required string ParentTaskId { get; init; }

    [JsonPropertyName("status")]
    public required TaskDecompositionStatus Status { get; init; }

    [JsonPropertyName("leafTasks")]
    [SchemaDescription("Execution-ready child tasks. Empty when status is ArchitectureGap.")]
    public required List<CodeGenerationLeafTask> LeafTasks { get; init; }

    [JsonPropertyName("architectureGaps")]
    [SchemaDescription("Missing architectural decisions. Empty when status is Ready.")]
    public required List<TaskArchitectureGap> ArchitectureGaps { get; init; }
}
