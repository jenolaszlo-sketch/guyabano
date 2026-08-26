using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning;

public interface ICodeGenerationTaskDecompositionParser
{
    ToolCallParseResult<CodeGenerationTaskDecomposition> Parse(
        Penghou.Baize.LlmResponse response);
}
