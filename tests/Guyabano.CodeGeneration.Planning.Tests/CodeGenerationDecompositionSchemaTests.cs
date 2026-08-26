using FluentAssertions;
using Penghou.Baize.Tools.Schema;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class CodeGenerationDecompositionSchemaTests
{
    [Fact]
    public void GenerateSchema_RequiresTypedLeafArtifactsAndStringStatus()
    {
        var schema = JsonSchemaGenerator
            .GenerateSchemaNode<CodeGenerationTaskDecomposition>()
            .AsObject();
        schema["properties"]!["status"]!["enum"]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .Should()
            .Equal("Ready", "ArchitectureGap");

        var leaf = schema["properties"]!["leafTasks"]!["items"]!;
        leaf["required"]!.AsArray()
            .Select(item => item!.GetValue<string>())
            .Should()
            .Contain(["implementationRequirements", "artifacts"]);
        leaf["properties"]!["artifacts"]!["items"]!["required"]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .Should()
            .Contain(["path", "namespace", "requirements"]);
    }
}
