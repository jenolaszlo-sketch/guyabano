using Penghou.Baize.Tools;
using Penghou.Baize;
using Penghou.Baize.Tools.Schema;
using Penghou.Nuwa;

namespace Guyabano.Llm.CodeGeneration;

public sealed class CodeGenerationResultParser()
    : LlmToolResultParserBase<CodeGenerationResult>(
        toolName: "emit_files",
        expectation: JsonSchemaExpectation.FromSchemaNode(
            JsonSchemaGenerator.GenerateSchemaNode<CodeGenerationResult>())),
      ICodeGenerationResultParser
{
    public ToolCallParseResult<CodeGenerationResult> Parse(
        LlmResponse response,
        string toolName) =>
        toolName.Equals("emit_files", StringComparison.Ordinal)
            ? Parse(response)
            : new NamedCodeGenerationResultParser(toolName)
                .Parse(response);

    private sealed class NamedCodeGenerationResultParser(string toolName)
        : LlmToolResultParserBase<CodeGenerationResult>(
            toolName,
            JsonSchemaExpectation.FromSchemaNode(
                JsonSchemaGenerator.GenerateSchemaNode<
                    CodeGenerationResult>()));
}
