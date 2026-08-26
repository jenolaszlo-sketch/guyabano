using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.Llm.CodeGeneration;

public interface ICodeGenerationResultParser
{
    ToolCallParseResult<CodeGenerationResult> Parse(LlmResponse response);

    ToolCallParseResult<CodeGenerationResult> Parse(
        LlmResponse response,
        string toolName);
}
