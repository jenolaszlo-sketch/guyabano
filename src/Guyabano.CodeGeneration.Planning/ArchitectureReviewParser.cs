using Penghou.Baize;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Nuwa;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureReviewParser
{
    private const string StructuredReviewName = "structured_review";

    private readonly StructuredReviewParser parser = new();

    public ToolCallParseResult<ArchitectureReview> Parse(LlmResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return parser.Parse(response with
        {
            ToolCalls =
            [
                new LlmToolCall(
                    "structured-output",
                    StructuredReviewName,
                    response.Content)
            ]
        });
    }

    private sealed class StructuredReviewParser()
        : LlmToolResultParserBase<ArchitectureReview>(
            StructuredReviewName,
            JsonSchemaExpectation.FromSchemaNode(
                JsonSchemaGenerator.GenerateSchemaNode<ArchitectureReview>()));
}
