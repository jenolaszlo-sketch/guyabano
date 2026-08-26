using FluentAssertions;
using Penghou.Baize;
using Guyabano.Llm.CodeGeneration;
using Guyabano.Messaging;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationProgressDiagnosticsTests
{
    [Fact]
    public void Create_WhenJsonWasRepaired_IncludesOnlyRelevantAttempts()
    {
        var outcome = CreateOutcome(
            jsonWasRepaired: true,
            [
                new LlmRepairAttempt(
                    "markdown-json-fence",
                    LlmRepairStatus.Skipped),
                new LlmRepairAttempt(
                    "salvage",
                    LlmRepairStatus.Succeeded,
                    "inserted missing '}'"),
                new LlmRepairAttempt(
                    "schema-guided-json-string-expansion",
                    LlmRepairStatus.NotApplicable)
            ]);

        var diagnostics =
            CodeGenerationProgressDiagnostics.Create(outcome);

        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.Code.Should().Be("json-repaired");
        diagnostic.Details.Should().ContainSingle()
            .Which.Should().Be("salvage: Succeeded");
    }

    [Fact]
    public void Create_WhenJsonWasNotRepaired_ReturnsNoDiagnostics()
    {
        var outcome = CreateOutcome(
            jsonWasRepaired: false,
            []);

        CodeGenerationProgressDiagnostics.Create(outcome)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Create_WhenSchemaValidationFailed_IncludesErrorDiagnostic()
    {
        var outcome = new CodeGenerationOutcome(
            Succeeded: false,
            Failure: CodeGenerationFailure.SchemaValidationFailed,
            Error: "$.files is required.",
            Model: "test-model",
            JsonWasRepaired: false,
            JsonRepairAttempts: [],
            WrittenFiles: [],
            SkippedFiles: []);

        var diagnostic = CodeGenerationProgressDiagnostics
            .Create(outcome)
            .Should()
            .ContainSingle()
            .Subject;

        diagnostic.Severity.Should().Be(
            WorkflowDiagnosticSeverity.Error);
        diagnostic.Code.Should().Be(
            "schema-validation-failed");
        diagnostic.Details.Should().ContainSingle()
            .Which.Should().Be("$.files is required.");
    }

    [Fact]
    public void Create_WhenToolCallIsMissingAfterLengthLimit_ExplainsExhaustion()
    {
        var outcome = new CodeGenerationOutcome(
            Succeeded: false,
            Failure: CodeGenerationFailure.MissingToolCall,
            Error: "No 'emit_files' tool call found.",
            Model: "deepseek-v4-flash",
            JsonWasRepaired: false,
            JsonRepairAttempts: [],
            WrittenFiles: [],
            SkippedFiles: [])
        {
            FinishReason = "length",
            Usage = new Penghou.Baize.LlmUsage(
                PromptTokens: 1355,
                CompletionTokens: 8000,
                TotalTokens: 9355)
        };

        var diagnostic = CodeGenerationProgressDiagnostics
            .Create(outcome)
            .Should()
            .ContainSingle()
            .Subject;

        diagnostic.Code.Should().Be("output-limit-exhausted");
        diagnostic.Summary.Should().Contain("output budget");
        diagnostic.Details.Should().Contain("Finish reason: length");
        diagnostic.Details.Should().Contain("Completion tokens: 8000");
    }

    private static CodeGenerationOutcome CreateOutcome(
        bool jsonWasRepaired,
        IReadOnlyList<LlmRepairAttempt> attempts) =>
        new(
            Succeeded: true,
            Failure: CodeGenerationFailure.None,
            Error: null,
            Model: "test-model",
            JsonWasRepaired: jsonWasRepaired,
            JsonRepairAttempts: attempts,
            WrittenFiles: [],
            SkippedFiles: []);
}
