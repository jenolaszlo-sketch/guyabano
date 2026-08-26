using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning;

public interface ICodeGenerationPlanParser
{
    ToolCallParseResult<CodeGenerationPlan> Parse(LlmResponse response);
}
