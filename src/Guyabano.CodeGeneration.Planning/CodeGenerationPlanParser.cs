using Penghou.Baize;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Nuwa;

namespace Guyabano.CodeGeneration.Planning;

public sealed class CodeGenerationPlanParser : ICodeGenerationPlanParser
{
    private const string StructuredPlanName = "structured_plan";

    private readonly StructuredPlanParser parser = new();

    public ToolCallParseResult<CodeGenerationPlan> Parse(LlmResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return parser.Parse(response with
        {
            ToolCalls =
            [
                new LlmToolCall(
                    "structured-output",
                    StructuredPlanName,
                    response.Content)
            ]
        });
    }

    private sealed class StructuredPlanParser()
        : LlmToolResultParserBase<CodeGenerationPlan>(
            toolName: StructuredPlanName,
            expectation: JsonSchemaExpectation.FromSchemaNode(
                JsonSchemaGenerator.GenerateSchemaNode<CodeGenerationPlan>()));
}
