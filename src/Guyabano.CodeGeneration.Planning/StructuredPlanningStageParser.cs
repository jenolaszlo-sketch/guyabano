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
        var result = Instance.Parse(response with
        {
            ToolCalls =
            [
                new LlmToolCall(
                    "structured-output",
                    ResultName,
                    response.Content)
            ]
        });
        if (result.Succeeded)
            return result;

        var repairAttempted = response.ContentRepairAttempts is { Count: > 0 };
        var diagnostics = response.ContentRepairDiagnostics;
        if (!repairAttempted && diagnostics is null)
            return result;

        var repairDetail = repairAttempted
            ? " Deterministic JSON repair was attempted, but the result was not accepted."
            : string.Empty;
        var shapeDetail = diagnostics?.ShapeErrors.Count > 0
            ? $" Repair shape errors: {string.Join(" ", diagnostics.ShapeErrors.Take(16))}"
            : string.Empty;
        return ToolCallParseResult<T>.Failed(
            result.Failure,
            $"{result.Error}{repairDetail}{shapeDetail}",
            result.Raw);
    }

    private sealed class Parser()
        : LlmToolResultParserBase<T>(
            ResultName,
            JsonSchemaExpectation.FromSchemaNode(
                JsonSchemaGenerator.GenerateSchemaNode<T>()));
}
