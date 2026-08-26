using Penghou.Baize;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Nuwa;

namespace Guyabano.CodeGeneration.Planning;

internal static class StructuredPlanningStageParser<T>
{
    private const string ResultName = "structured_planning_stage";
    private static readonly Parser Instance = new();

    public static ToolCallParseResult<T> Parse(LlmResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Instance.Parse(response with
        {
            ToolCalls =
            [
                new LlmToolCall(
                    "structured-output",
                    ResultName,
                    response.Content)
            ]
        });
    }

    private sealed class Parser()
        : LlmToolResultParserBase<T>(
            ResultName,
            JsonSchemaExpectation.FromSchemaNode(
                JsonSchemaGenerator.GenerateSchemaNode<T>()));
}
