using Penghou.Baize;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Nuwa;

namespace Guyabano.CodeGeneration.Planning;

public sealed class CodeGenerationTaskDecompositionParser()
    : LlmToolResultParserBase<CodeGenerationTaskDecomposition>(
        CodeGenerationTaskDecompositionService.ToolName,
        JsonSchemaExpectation.FromSchemaNode(
            JsonSchemaGenerator.GenerateSchemaNode<
                CodeGenerationTaskDecomposition>())),
      ICodeGenerationTaskDecompositionParser;
