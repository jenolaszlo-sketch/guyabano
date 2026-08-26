using System.Text.Json.Nodes;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Penghou.Baize.Tools.Schema;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class CodeGenerationPlanSchemaTests
{
    [Fact]
    public void GenerateSchema_DescribesExecutablePlanShape()
    {
        var schema = JsonSchemaGenerator
            .GenerateSchemaNode<CodeGenerationPlan>()
            .AsObject();
        var properties = schema["properties"]!.AsObject();

        properties.Should().ContainKey("solution");
        properties.Should().ContainKey("architectureNotes");
        properties.Should().ContainKey("mission");
        properties.Should().ContainKey("useCases");

        var useCaseProperties = properties["useCases"]!["items"]![
            "properties"]!.AsObject();
        useCaseProperties.Should().ContainKey("boundedContext");
        useCaseProperties.Should().ContainKey("acceptanceCriterionIds");

        var noteSchema = properties["architectureNotes"]!["items"]!.AsObject();
        noteSchema["properties"]!["category"]!["enum"]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .Should()
            .Contain("InferredDomainConstraint");

        var projectSchema = properties["projects"]!["items"]!.AsObject();
        var requiredProjectProperties = projectSchema["required"]!
            .AsArray()
            .Select(item => item!.GetValue<string>());
        requiredProjectProperties.Should().Contain([
            "targetFramework",
            "packages"
        ]);

        var taskSchema = properties["tasks"]!["items"]!.AsObject();
        var taskProperties = taskSchema["properties"]!.AsObject();
        taskProperties["executionKind"]!["enum"]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .Should()
            .Equal("Scaffolding", "CodeGeneration");

        taskSchema["required"]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .Should()
            .NotContain("moduleId");
    }
}
