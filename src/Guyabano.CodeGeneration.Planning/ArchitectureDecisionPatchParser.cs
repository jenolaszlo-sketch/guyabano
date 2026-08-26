using Penghou.Baize;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Nuwa;

namespace Guyabano.CodeGeneration.Planning;

public sealed class ArchitectureDecisionPatchParser()
    : LlmToolResultParserBase<ArchitectureDecisionPatch>(
        ArchitectureDecisionIntegrator.ToolName,
        JsonSchemaExpectation.FromSchemaNode(
            JsonSchemaGenerator.GenerateSchemaNode<ArchitectureDecisionPatch>()));
