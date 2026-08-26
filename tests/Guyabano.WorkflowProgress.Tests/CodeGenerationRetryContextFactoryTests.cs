using FluentAssertions;
using Penghou.Baize;
using Guyabano.CodeGeneration.Validation;
using Guyabano.Llm.CodeGeneration;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationRetryContextFactoryTests
{
    [Fact]
    public void Create_IncludesFailureRepairAndFileDiagnostics()
    {
        var outcome = new CodeGenerationOutcome(
            false,
            CodeGenerationFailure.FileValidationFailed,
            "Generated files failed syntax validation.",
            "small",
            true,
            [
                new LlmRepairAttempt(
                    "markdown-json-fence",
                    LlmRepairStatus.Succeeded),
                new LlmRepairAttempt(
                    "salvage",
                    LlmRepairStatus.NotApplicable)
            ],
            [Path.Combine("generated", "src", "Todo", "Broken.cs")],
            [])
        {
            FileValidation = new GeneratedFilesValidationResult(
            [
                new GeneratedFileValidationResult(
                    "src/Todo/Broken.cs",
                    true,
                    [
                        new FileValidationDiagnostic(
                            "csharp-syntax",
                            "CS1513",
                            FileValidationSeverity.Error,
                            "} expected",
                            "src/Todo/Broken.cs",
                            4,
                            1)
                    ])
            ])
        };

        var retry = CodeGenerationRetryContextFactory.Create(
            outcome,
            1,
            "small",
            "generated");

        retry.Failure.Should().Be("FileValidationFailed");
        retry.Diagnostics.Should().Contain(item =>
            item.Contains("markdown-json-fence"));
        retry.Diagnostics.Should().Contain(item =>
            item.Contains("CS1513"));
        retry.Diagnostics.Should().NotContain(item =>
            item.Contains("not needed"));
        retry.WrittenFiles.Should().ContainSingle()
            .Which.Should().Be("src/Todo/Broken.cs");
    }
}
