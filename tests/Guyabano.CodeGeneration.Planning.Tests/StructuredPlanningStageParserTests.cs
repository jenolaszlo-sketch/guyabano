using FluentAssertions;
using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class StructuredPlanningStageParserTests
{
    [Fact]
    public void Parse_RejectsValidJsonForTheWrongPlanningStage()
    {
        var response = new LlmResponse(Content: "{}");

        var result = StructuredPlanningStageParser<DomainDiscovery>
            .Parse(response);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(
            ToolCallParseFailure.SchemaValidationFailed);
        result.Error.Should().Contain("$.mission is required");
    }

    [Fact]
    public void Parse_RejectsConcatenatedPlanningDocuments()
    {
        var response = new LlmResponse(Content: "{} {}");

        var result = StructuredPlanningStageParser<DomainDiscovery>
            .Parse(response);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(ToolCallParseFailure.InvalidJson);
    }

    [Fact]
    public void Parse_PreservesRepairedButMismatchedDiagnostics()
    {
        var response = new LlmResponse(
            Content: "{}",
            ContentRepairAttempts:
            [
                new LlmRepairAttempt(
                    "content/tolerant-recovery",
                    LlmRepairStatus.Succeeded)
            ])
        {
            ContentRepairDiagnostics = new LlmJsonRepairDiagnostics(
                LlmRepairShapeStatus.Mismatched,
                ["$.mission is required."])
        };

        var result = StructuredPlanningStageParser<DomainDiscovery>
            .Parse(response);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(
            ToolCallParseFailure.SchemaValidationFailed);
        result.Error.Should().Contain("repair was attempted");
        result.Error.Should().Contain("$.mission is required");
    }

    [Fact]
    public void Parse_DefaultsOmittedRelationshipCollectionsToEmpty()
    {
        const string json = """
            {
              "boundedContextName": "Todos",
              "components": [
                {
                  "name": "TodoService",
                  "kind": "Service",
                  "moduleName": "Todos.Application",
                  "projectName": "Todos.Application",
                  "files": ["src/Todos.Application/TodoService.cs"],
                  "responsibilities": ["Coordinate todos."],
                  "capabilityNames": ["ManageTodos"],
                  "acceptanceCriterionIds": ["AC-1"],
                  "lifetime": "Scoped",
                  "complexityPoints": 2,
                  "verificationKinds": ["Compilation"]
                }
              ],
              "decisions": [],
              "inferredDefaults": []
            }
            """;

        var result = StructuredPlanningStageParser<
                BoundedContextComponentManifest>
            .Parse(new LlmResponse(Content: json));

        result.Succeeded.Should().BeTrue(result.Error);
        var component = result.Value!.Components.Should()
            .ContainSingle().Subject;
        component.DefinesContractNames.Should().BeEmpty();
        component.ImplementsPortNames.Should().BeEmpty();
        component.ConsumesContractNames.Should().BeEmpty();
        component.UsesConcreteComponentNames.Should().BeEmpty();
        component.RegistersImplementationNames.Should().BeEmpty();
        component.TestsComponentNames.Should().BeEmpty();
    }
}
