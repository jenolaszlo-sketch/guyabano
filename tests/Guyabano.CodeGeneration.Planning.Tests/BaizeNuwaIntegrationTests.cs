using FluentAssertions;
using Penghou.Baize;
using Penghou.Baize.Tools;
using Penghou.Baize.Tools.Schema;
using Penghou.Nuwa;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class BaizeNuwaIntegrationTests
{
    [Fact]
    public async Task RepairAndParse_ExpandsAndCoercesEncodedStringArray()
    {
        var schema = JsonSchemaGenerator.GenerateSchemaJson<StagedComponent>();
        var repairer = new LlmStructuredOutputRepairer(
            JsonRepairPipeline.Create());
        var repaired = await repairer.RepairAsync(
            new LlmResponse(
                """
                {
                  "name": "TodoContracts",
                  "kind": "Contracts",
                  "moduleName": "Todos.Contracts",
                  "projectName": "Todos.Contracts",
                  "files": "[1, 2]",
                  "responsibilities": ["Define todo contracts."],
                  "capabilityNames": ["ManageTodos"],
                  "acceptanceCriterionIds": ["AC-1"],
                  "lifetime": "Singleton",
                  "complexityPoints": 1,
                  "verificationKinds": ["Compilation"]
                }
                """),
            LlmResponseFormat.JsonSchema(schema),
            TestContext.Current.CancellationToken);

        repaired.ContentWasRepaired.Should().BeTrue();
        repaired.ContentRepairDiagnostics.Should().NotBeNull();
        repaired.ContentRepairDiagnostics!.IsRepairAccepted.Should().BeTrue();
        repaired.ContentRepairAttempts.Should().Contain(attempt =>
            attempt.Name ==
                "content/schema-guided-json-string-expansion" &&
            attempt.Status == LlmRepairStatus.Succeeded);
        repaired.ContentRepairAttempts.Should().Contain(attempt =>
            attempt.Name ==
                "content/schema-guided-scalar-to-string" &&
            attempt.Status == LlmRepairStatus.Succeeded);

        var parsed = new Parser().Parse(repaired with
        {
            ToolCalls =
            [
                new LlmToolCall(
                    "structured-output",
                    Parser.ParsedToolName,
                    repaired.Content)
            ]
        });

        parsed.Succeeded.Should().BeTrue(parsed.Error);
        parsed.Value!.Files.Should().Equal("1", "2");
    }

    [Fact]
    public async Task Normalize_PreservesRejectedArgumentsForGuyabanoRecovery()
    {
        var pipeline = JsonRepairPipeline.Create();
        var normalizer = new LlmResponseNormalizer(
            new ContentToolCallExtractor(pipeline),
            pipeline);
        var schema = JsonSchemaGenerator.GenerateSchemaJson<StagedComponent>();
        const string argumentsJson = """{"unexpected":1}""";
        var normalized = await normalizer.NormalizeAsync(
            new LlmResponse(
                Content: string.Empty,
                ToolCalls:
                [
                    new LlmToolCall(
                        "call-1",
                        Parser.ParsedToolName,
                        argumentsJson)
                ]),
            [
                new LlmTool(
                    Parser.ParsedToolName,
                    "Returns one staged component.",
                    schema)
            ],
            TestContext.Current.CancellationToken);

        var call = normalized.ToolCalls.Should().ContainSingle().Subject;
        call.NormalizationStatus.Should().Be(
            LlmToolCallNormalizationStatus.InvalidArguments);
        call.ArgumentsJson.Should().Be(argumentsJson);
        call.JsonRepairDiagnostics.Should().NotBeNull();
        call.JsonRepairDiagnostics!.IsRepairAccepted.Should().BeFalse();

        var parsed = new Parser().Parse(normalized);
        parsed.Succeeded.Should().BeFalse();
        parsed.Failure.Should().Be(
            ToolCallParseFailure.SchemaValidationFailed);
    }

    private sealed class Parser()
        : LlmToolResultParserBase<StagedComponent>(
            ParsedToolName,
            JsonSchemaExpectation.FromSchemaJson(
                JsonSchemaGenerator.GenerateSchemaJson<StagedComponent>())!)
    {
        internal const string ParsedToolName = "structured_planning_stage";
    }
}
